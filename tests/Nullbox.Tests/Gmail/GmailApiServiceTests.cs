using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Gmail;

namespace Nullbox.Tests.Gmail;

public sealed class GmailApiServiceTests
{
    private readonly FakeGmailApi _api = new();
    private readonly List<TimeSpan> _delays = [];

    private GmailApiService CreateService(Action<GmailOptions>? configure = null)
    {
        var options = new GmailOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(10) };
        configure?.Invoke(options);
        var retry = new GmailRetry(options, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });
        return new GmailApiService(_api.CreateService(), Options.Create(options), NullLogger<GmailApiService>.Instance, retry);
    }

    private static IEnumerable<string> Ids(int count, string prefix = "m") => Enumerable.Range(0, count).Select(i => $"{prefix}{i}");

    // ---- search -----------------------------------------------------------------------------

    [Fact]
    public async Task Search_follows_page_tokens_and_sends_the_query()
    {
        _api.On("GET /messages", req => req.Query("pageToken") switch
        {
            null => FakeGmailApi.Json(new { messages = new[] { new { id = "a" }, new { id = "b" } }, nextPageToken = "p2" }),
            "p2" => FakeGmailApi.Json(new { messages = new[] { new { id = "c" } } }),
            _ => FakeGmailApi.Error(HttpStatusCode.BadRequest),
        });

        var ids = await CreateService().SearchAsync("in:inbox is:unread");

        Assert.Equal(["a", "b", "c"], ids);
        Assert.All(_api.To("GET /messages"), r => Assert.Equal("in:inbox is:unread", r.Query("q")));
        Assert.Equal("messages(id),nextPageToken", _api.Requests[0].Query("fields"));
        Assert.Equal("500", _api.Requests[0].Query("maxResults"));
    }

    [Fact]
    public async Task Search_stops_at_max_results()
    {
        _api.On("GET /messages", _ => FakeGmailApi.Json(new
        {
            messages = new[] { new { id = "a" }, new { id = "b" }, new { id = "c" } },
            nextPageToken = "more",
        }));

        var ids = await CreateService().SearchAsync("label:x", maxResults: 2);

        Assert.Equal(["a", "b"], ids);
        Assert.Single(_api.Requests);
        Assert.Equal("2", _api.Requests[0].Query("maxResults"));
    }

    [Fact]
    public async Task Search_with_no_matches_returns_empty()
    {
        _api.OnJson("GET /messages", new { resultSizeEstimate = 0 });

        Assert.Empty(await CreateService().SearchAsync("nothing"));
    }

    // ---- get message ----------------------------------------------------------------------------

    [Fact]
    public async Task Get_metadata_maps_headers_and_requests_only_metadata()
    {
        _api.OnJson("GET /messages/m1", new
        {
            id = "m1",
            threadId = "t1",
            labelIds = new[] { "INBOX", "UNREAD" },
            snippet = "Hi there",
            internalDate = "1764219600000",
            payload = new
            {
                headers = new[]
                {
                    new { name = "From", value = "Shop <deals@shop.example>" },
                    new { name = "subject", value = "50% off" },
                    new { name = "Date", value = "Thu, 27 Nov 2025 05:00:00 +0000" },
                    new { name = "List-Unsubscribe", value = "<mailto:u@shop.example>" },
                    new { name = "To", value = "Me <me+shop@gmail.com>" },
                    new { name = "Cc", value = "ann@y.example" },
                },
            },
        });

        var message = await CreateService().GetMessageAsync("m1");

        Assert.Equal("Shop <deals@shop.example>", message.From);
        Assert.Equal("50% off", message.Subject);
        Assert.Equal(("Me <me+shop@gmail.com>", "ann@y.example"), (message.To, message.Cc));
        Assert.Equal("t1", message.ThreadId);
        Assert.Equal(["INBOX", "UNREAD"], message.LabelIds);
        Assert.True(message.HasUnsubscribe);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1764219600000), message.InternalDate);
        Assert.Null(message.Body);

        var request = Assert.Single(_api.Requests);
        Assert.Equal("metadata", request.Query("format"));
        Assert.Contains("List-Unsubscribe", request.Uri.Query);
        Assert.Contains("metadataHeaders=Cc", request.Uri.Query);
        Assert.Equal("id,threadId,labelIds,snippet,internalDate,payload/headers", request.Query("fields"));
    }

    [Fact]
    public async Task Get_full_extracts_the_plain_text_part_and_truncates_it()
    {
        _api.OnJson("GET /messages/m2", new
        {
            id = "m2",
            payload = new
            {
                mimeType = "multipart/alternative",
                parts = new object[]
                {
                    new { mimeType = "text/html", body = new { data = B64("<p>html version</p>") } },
                    new { mimeType = "text/plain", body = new { data = B64("Hello world, this is the plain body.") } },
                },
            },
        });

        var message = await CreateService(o => o.BodyMaxChars = 11).GetMessageAsync("m2", MessageFormat.Full);

        Assert.Equal("Hello world", message.Body);
        // "full" is Gmail's default format, so the client may omit it; it must not ask for metadata.
        Assert.NotEqual("metadata", _api.Requests[0].Query("format"));
        Assert.Equal("id,threadId,labelIds,snippet,internalDate,payload", _api.Requests[0].Query("fields"));
    }

    [Fact]
    public async Task Get_full_falls_back_to_html_without_tags()
    {
        _api.OnJson("GET /messages/m3", new
        {
            id = "m3",
            payload = new
            {
                mimeType = "text/html",
                body = new { data = B64("<html><style>p{}</style><p>Fish &amp; chips</p>\n<b>today</b></html>") },
            },
        });

        var message = await CreateService().GetMessageAsync("m3", MessageFormat.Full);

        Assert.Equal("Fish & chips today", message.Body);
    }

    [Fact]
    public void Base64url_decoding_handles_missing_padding_and_url_alphabet()
    {
        var text = "subjects?>>~ünïcode";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(text, GmailApiService.DecodeBase64Url(encoded));
    }

    // ---- labels ---------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureLabel_returns_an_existing_label_case_insensitively_without_creating()
    {
        _api.OnJson("GET /labels", new { labels = new[] { new { id = "Label_7", name = "Archived/Promo" }, new { id = "INBOX", name = "INBOX" } } });
        var service = CreateService();

        var id = await service.EnsureLabelAsync("archived/promo");
        var again = await service.EnsureLabelAsync("Archived/Promo");

        Assert.Equal("Label_7", id);
        Assert.Equal("Label_7", again);
        Assert.Single(_api.To("GET /labels")); // cached
        Assert.Empty(_api.To("POST /labels"));
    }

    [Fact]
    public async Task EnsureLabel_creates_a_missing_label_once()
    {
        _api.OnJson("GET /labels", new { labels = new[] { new { id = "INBOX", name = "INBOX" } } });
        _api.On("POST /labels", req => FakeGmailApi.Json(new { id = "Label_9", name = req.Json.GetProperty("name").GetString() }));
        var service = CreateService();

        var id = await service.EnsureLabelAsync("nullbox/archived");
        var again = await service.EnsureLabelAsync("nullbox/archived");

        Assert.Equal("Label_9", id);
        Assert.Equal("Label_9", again);
        var create = Assert.Single(_api.To("POST /labels"));
        Assert.Equal("nullbox/archived", create.Json.GetProperty("name").GetString());
        Assert.Equal("labelShow", create.Json.GetProperty("labelListVisibility").GetString());
    }

    // ---- batch modify, mark read, archive ------------------------------------------------------

    [Fact]
    public async Task BatchModify_chunks_at_1000_and_deduplicates()
    {
        _api.On("POST /messages/batchModify", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var ids = Ids(2500).Concat(Ids(10)); // 10 duplicates

        await CreateService().BatchModifyAsync(ids, ["Label_1"], ["INBOX"]);

        var calls = _api.To("POST /messages/batchModify").ToList();
        Assert.Equal([1000, 1000, 500], calls.Select(c => c.JsonStrings("ids").Count));
        Assert.Equal(2500, calls.SelectMany(c => c.JsonStrings("ids")).Distinct().Count());
        Assert.All(calls, c =>
        {
            Assert.Equal(["Label_1"], c.JsonStrings("addLabelIds"));
            Assert.Equal(["INBOX"], c.JsonStrings("removeLabelIds"));
        });
    }

    [Fact]
    public async Task Empty_inputs_make_no_requests()
    {
        var service = CreateService(o => (o.AllowDelete, o.AllowPermanentDelete) = (true, true));

        await service.BatchModifyAsync([], ["L"], null);
        await service.BatchModifyAsync(Ids(3), null, null);
        await service.MarkReadAsync([]);
        await service.ArchiveAsync([], "label");
        await service.TrashAsync([]);
        await service.BatchDeleteAsync([]);

        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task MarkRead_removes_UNREAD()
    {
        _api.On("POST /messages/batchModify", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateService().MarkReadAsync(["a", "b"]);

        var call = Assert.Single(_api.Requests);
        Assert.Equal(["a", "b"], call.JsonStrings("ids"));
        Assert.Equal(["UNREAD"], call.JsonStrings("removeLabelIds"));
        Assert.Empty(call.JsonStrings("addLabelIds"));
    }

    [Fact]
    public async Task Archive_removes_INBOX_and_applies_the_label()
    {
        _api.OnJson("GET /labels", new { labels = new[] { new { id = "Label_3", name = "cleanup/2026-11-27" } } });
        _api.On("POST /messages/batchModify", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateService().ArchiveAsync(["a", "b"], "cleanup/2026-11-27");

        var modify = Assert.Single(_api.To("POST /messages/batchModify"));
        Assert.Equal(["Label_3"], modify.JsonStrings("addLabelIds"));
        Assert.Equal(["INBOX"], modify.JsonStrings("removeLabelIds"));
    }

    [Fact]
    public async Task Archive_without_label_only_removes_INBOX()
    {
        _api.On("POST /messages/batchModify", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateService().ArchiveAsync(["a"], label: null);

        var modify = Assert.Single(_api.Requests);
        Assert.Empty(modify.JsonStrings("addLabelIds"));
        Assert.Equal(["INBOX"], modify.JsonStrings("removeLabelIds"));
    }

    // ---- trash and delete -----------------------------------------------------------------------

    [Fact]
    public async Task Trash_is_refused_while_deletes_are_locked_and_sends_nothing()
    {
        var ex = await Assert.ThrowsAsync<DeleteLockedException>(() => CreateService().TrashAsync(["a", "b"]));

        Assert.Contains("AllowDelete", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Trash_moves_each_distinct_message_to_trash_when_unlocked()
    {
        _api.On("POST /messages/*/trash", req => FakeGmailApi.Json(new { id = req.Route.Split('/')[2] }));

        await CreateService(o => o.AllowDelete = true).TrashAsync(["a", "b", "a"]);

        Assert.Equal(["POST /messages/a/trash", "POST /messages/b/trash"], _api.Requests.Select(r => r.Route));
    }

    [Fact]
    public async Task BatchDelete_is_refused_by_default_and_sends_nothing()
    {
        await Assert.ThrowsAsync<DeleteLockedException>(() => CreateService().BatchDeleteAsync(Ids(5)));

        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task BatchDelete_stays_locked_even_when_permanent_delete_is_allowed()
    {
        await Assert.ThrowsAsync<DeleteLockedException>(() => CreateService(o => o.AllowPermanentDelete = true).BatchDeleteAsync(Ids(5)));

        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task BatchDelete_still_needs_permanent_delete_once_unlocked()
    {
        await Assert.ThrowsAsync<PermanentDeleteDisabledException>(() => CreateService(o => o.AllowDelete = true).BatchDeleteAsync(Ids(5)));

        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task BatchDelete_chunks_when_enabled()
    {
        _api.On("POST /messages/batchDelete", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateService(o => (o.AllowDelete, o.AllowPermanentDelete) = (true, true)).BatchDeleteAsync(Ids(1001));

        Assert.Equal([1000, 1], _api.To("POST /messages/batchDelete").Select(c => c.JsonStrings("ids").Count));
    }

    // ---- retry ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null)]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded")]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded")]
    public async Task Transient_failures_are_retried_with_backoff(HttpStatusCode status, string? reason)
    {
        var calls = 0;
        _api.On("POST /messages/batchModify", _ => ++calls <= 2
            ? FakeGmailApi.Error(status, reason)
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        await CreateService().MarkReadAsync(["a"]);

        Assert.Equal(3, calls);
        Assert.Equal(2, _delays.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, null)]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions")]
    public async Task Permanent_failures_are_not_retried(HttpStatusCode status, string? reason)
    {
        _api.On("POST /messages/batchModify", _ => FakeGmailApi.Error(status, reason));

        var ex = await Assert.ThrowsAsync<Google.GoogleApiException>(() => CreateService().MarkReadAsync(["a"]));

        Assert.Equal(status, ex.HttpStatusCode);
        Assert.Single(_api.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Retries_give_up_after_MaxRetries()
    {
        _api.On("GET /messages", _ => FakeGmailApi.Error(HttpStatusCode.TooManyRequests));

        await Assert.ThrowsAsync<Google.GoogleApiException>(() => CreateService(o => o.MaxRetries = 3).SearchAsync("x"));

        Assert.Equal(4, _api.Requests.Count);
        Assert.Equal(3, _delays.Count);
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var options = new GmailOptions { RetryBaseDelay = TimeSpan.FromSeconds(1), RetryMaxDelay = TimeSpan.FromSeconds(8) };
        var retry = new GmailRetry(options, random: new MaxRandom());

        Assert.Equal(
            [1, 2, 4, 8, 8],
            Enumerable.Range(0, 5).Select(a => (int)Math.Round(retry.DelayFor(a).TotalSeconds)));
    }

    private static string B64(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>NextDouble() just below 1, so the jittered delay sits at its ceiling.</summary>
    private sealed class MaxRandom : Random
    {
        public override double NextDouble() => 0.999999;
    }

    // ---- history ----------------------------------------------------------------------------

    [Fact]
    public async Task Current_history_id_comes_from_the_profile()
    {
        _api.OnJson("GET /profile", new { historyId = "98765" });

        Assert.Equal(98765UL, await CreateService().GetCurrentHistoryIdAsync());
        Assert.Equal("historyId", _api.Requests[0].Query("fields"));
    }

    [Fact]
    public async Task Inbox_changes_follow_pages_keep_inbox_messages_once_and_return_the_latest_id()
    {
        _api.On("GET /history", req => req.Query("pageToken") switch
        {
            null => FakeGmailApi.Json(new
            {
                history = new object[]
                {
                    new { messagesAdded = new[] { new { message = new { id = "a", labelIds = new[] { "INBOX", "UNREAD" } } } } },
                    new { messagesAdded = new[] { new { message = new { id = "sent", labelIds = new[] { "SENT" } } } } },
                },
                historyId = "120",
                nextPageToken = "p2",
            }),
            "p2" => FakeGmailApi.Json(new
            {
                history = new object[]
                {
                    new { messagesAdded = new[] { new { message = new { id = "a", labelIds = new[] { "INBOX" } } }, new { message = new { id = "b", labelIds = new[] { "INBOX" } } } } },
                    new { },
                },
                historyId = "130",
            }),
            _ => FakeGmailApi.Error(HttpStatusCode.BadRequest),
        });

        var changes = await CreateService().GetInboxChangesAsync(100);

        Assert.Equal(["a", "b"], changes.AddedMessageIds);
        Assert.Equal(130UL, changes.HistoryId);
        var first = _api.To("GET /history").First();
        Assert.Equal("100", first.Query("startHistoryId"));
        Assert.Equal("INBOX", first.Query("labelId"));
        Assert.Equal("messageAdded", first.Query("historyTypes"));
    }

    [Fact]
    public async Task Inbox_changes_with_no_history_keep_the_start_id()
    {
        _api.OnJson("GET /history", new { });

        var changes = await CreateService().GetInboxChangesAsync(55);

        Assert.Empty(changes.AddedMessageIds);
        Assert.Equal(55UL, changes.HistoryId);
    }

    [Fact]
    public async Task Expired_history_404_becomes_a_typed_exception_without_retries()
    {
        _api.On("GET /history", _ => FakeGmailApi.Error(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GmailHistoryExpiredException>(() => CreateService().GetInboxChangesAsync(7));

        Assert.Equal(7UL, ex.StartHistoryId);
        Assert.Single(_api.To("GET /history"));
    }
}
