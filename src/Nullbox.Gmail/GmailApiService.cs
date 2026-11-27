using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;

namespace Nullbox.Gmail;

/// <summary><see cref="IGmailService"/> over the official Gmail API client, acting on the authenticated user (<c>me</c>).</summary>
public sealed partial class GmailApiService : IGmailService
{
    public const int MaxIdsPerBatch = 1000;
    private const string Me = "me";
    private const int ListPageSize = 500;

    private static readonly string[] MetadataHeaders = ["From", "Subject", "Date", "List-Unsubscribe"];

    private readonly GmailService _gmail;
    private readonly GmailOptions _options;
    private readonly GmailRetry _retry;
    private readonly ILogger<GmailApiService> _logger;
    private readonly ConcurrentDictionary<string, string> _labelIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _labelLock = new(1, 1);

    public GmailApiService(GmailService gmail, IOptions<GmailOptions> options, ILogger<GmailApiService> logger, GmailRetry? retry = null)
    {
        _gmail = gmail;
        _options = options.Value;
        _logger = logger;
        _retry = retry ?? new GmailRetry(_options);
    }

    public async Task<IReadOnlyList<string>> SearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = new List<string>();
        string? pageToken = null;

        do
        {
            var request = _gmail.Users.Messages.List(Me);
            request.Q = query;
            request.IncludeSpamTrash = false;
            request.MaxResults = maxResults is { } cap ? Math.Min(ListPageSize, cap - ids.Count) : ListPageSize;
            request.PageToken = pageToken;
            request.Fields = "messages(id),nextPageToken";

            var page = await _retry.ExecuteAsync(ct => request.ExecuteAsync(ct), cancellationToken);
            ids.AddRange(page.Messages?.Select(m => m.Id) ?? []);
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && (maxResults is null || ids.Count < maxResults));

        _logger.LogDebug("Search matched {Count} messages", ids.Count);
        return maxResults is { } limit && ids.Count > limit ? ids[..limit] : ids;
    }

    public async Task<GmailMessage> GetMessageAsync(string messageId, MessageFormat format = MessageFormat.Metadata, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var request = _gmail.Users.Messages.Get(Me, messageId);
        if (format == MessageFormat.Metadata)
        {
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            request.MetadataHeaders = new Repeatable<string>(MetadataHeaders);
            request.Fields = "id,threadId,labelIds,snippet,internalDate,payload/headers";
        }
        else
        {
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            request.Fields = "id,threadId,labelIds,snippet,internalDate,payload";
        }

        var message = await _retry.ExecuteAsync(ct => request.ExecuteAsync(ct), cancellationToken);
        return Map(message, format == MessageFormat.Full ? _options.BodyMaxChars : null);
    }

    public async Task<ulong> GetCurrentHistoryIdAsync(CancellationToken cancellationToken = default)
    {
        var request = _gmail.Users.GetProfile(Me);
        request.Fields = "historyId";
        var profile = await _retry.ExecuteAsync(ct => request.ExecuteAsync(ct), cancellationToken);
        return profile.HistoryId ?? throw new InvalidOperationException("Gmail returned a profile without a historyId.");
    }

    public async Task<GmailHistoryChanges> GetInboxChangesAsync(ulong startHistoryId, CancellationToken cancellationToken = default)
    {
        var added = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var latest = startHistoryId;
        string? pageToken = null;

        do
        {
            var request = _gmail.Users.History.List(Me);
            request.StartHistoryId = startHistoryId;
            request.LabelId = GmailLabels.Inbox;
            request.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
            request.MaxResults = ListPageSize;
            request.PageToken = pageToken;
            request.Fields = "history(messagesAdded(message(id,labelIds))),historyId,nextPageToken";

            ListHistoryResponse page;
            try
            {
                page = await _retry.ExecuteAsync(ct => request.ExecuteAsync(ct), cancellationToken);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                throw new GmailHistoryExpiredException(startHistoryId, ex);
            }

            foreach (var message in (page.History ?? []).SelectMany(h => h.MessagesAdded ?? []).Select(m => m.Message))
            {
                var inInbox = message?.LabelIds is null || message.LabelIds.Contains(GmailLabels.Inbox);
                if (message?.Id is { } id && inInbox && seen.Add(id))
                {
                    added.Add(id);
                }
            }

            latest = Math.Max(latest, page.HistoryId ?? latest);
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        _logger.LogDebug("History since {Start} added {Count} inbox messages", startHistoryId, added.Count);
        return new GmailHistoryChanges(added, latest);
    }

    public async Task<string> EnsureLabelAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_labelIds.TryGetValue(name, out var cached))
        {
            return cached;
        }

        await _labelLock.WaitAsync(cancellationToken);
        try
        {
            if (_labelIds.TryGetValue(name, out cached))
            {
                return cached;
            }

            var list = await _retry.ExecuteAsync(ct => _gmail.Users.Labels.List(Me).ExecuteAsync(ct), cancellationToken);
            foreach (var label in list.Labels ?? [])
            {
                _labelIds[label.Name] = label.Id;
            }

            if (_labelIds.TryGetValue(name, out cached))
            {
                return cached;
            }

            var created = await _retry.ExecuteAsync(
                ct => _gmail.Users.Labels.Create(
                    new Label { Name = name, LabelListVisibility = "labelShow", MessageListVisibility = "show" },
                    Me).ExecuteAsync(ct),
                cancellationToken);
            _logger.LogInformation("Created Gmail label {Label}", name);
            _labelIds[created.Name ?? name] = created.Id;
            return created.Id;
        }
        finally
        {
            _labelLock.Release();
        }
    }

    public async Task BatchModifyAsync(
        IEnumerable<string> messageIds,
        IEnumerable<string>? addLabelIds,
        IEnumerable<string>? removeLabelIds,
        CancellationToken cancellationToken = default)
    {
        var add = addLabelIds?.Distinct().ToList() ?? [];
        var remove = removeLabelIds?.Distinct().ToList() ?? [];
        if (add.Count == 0 && remove.Count == 0)
        {
            return;
        }

        foreach (var chunk in Chunks(messageIds))
        {
            var body = new BatchModifyMessagesRequest
            {
                Ids = chunk,
                AddLabelIds = add.Count > 0 ? add : null,
                RemoveLabelIds = remove.Count > 0 ? remove : null,
            };
            await _retry.ExecuteAsync(ct => _gmail.Users.Messages.BatchModify(body, Me).ExecuteAsync(ct), cancellationToken);
            _logger.LogInformation("Modified {Count} messages (+{Add} -{Remove})", chunk.Count, add.Count, remove.Count);
        }
    }

    public Task MarkReadAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
        => BatchModifyAsync(messageIds, null, [GmailLabels.Unread], cancellationToken);

    public async Task ArchiveAsync(IEnumerable<string> messageIds, string? label, CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        string[]? add = string.IsNullOrWhiteSpace(label) ? null : [await EnsureLabelAsync(label, cancellationToken)];
        await BatchModifyAsync(ids, add, [GmailLabels.Inbox], cancellationToken);
    }

    public async Task TrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Distinct().ToList();
        // Gmail has no batch trash endpoint; trash is applied per message.
        foreach (var id in ids)
        {
            await _retry.ExecuteAsync(ct => _gmail.Users.Messages.Trash(Me, id).ExecuteAsync(ct), cancellationToken);
        }

        if (ids.Count > 0)
        {
            _logger.LogInformation("Trashed {Count} messages", ids.Count);
        }
    }

    public async Task BatchDeleteAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (!_options.AllowPermanentDelete)
        {
            throw new PermanentDeleteDisabledException();
        }

        foreach (var chunk in Chunks(messageIds))
        {
            var body = new BatchDeleteMessagesRequest { Ids = chunk };
            await _retry.ExecuteAsync(ct => _gmail.Users.Messages.BatchDelete(body, Me).ExecuteAsync(ct), cancellationToken);
            _logger.LogWarning("Permanently deleted {Count} messages", chunk.Count);
        }
    }

    /// <summary>De-duplicated ids in chunks of at most <see cref="MaxIdsPerBatch"/>.</summary>
    public static IEnumerable<List<string>> Chunks(IEnumerable<string> ids)
        => ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Chunk(MaxIdsPerBatch).Select(c => c.ToList());

    internal static GmailMessage Map(Message message, int? bodyMaxChars)
    {
        var headers = message.Payload?.Headers ?? [];
        string? Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        DateTimeOffset? internalDate = message.InternalDate is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var body = bodyMaxChars is { } max ? Truncate(ExtractBody(message.Payload), max) : null;

        return new GmailMessage(
            message.Id,
            message.ThreadId,
            Header("From"),
            Header("Subject"),
            Header("Date"),
            Header("List-Unsubscribe"),
            message.LabelIds?.ToList() ?? [],
            message.Snippet,
            internalDate,
            body);
    }

    /// <summary>Prefers the first text/plain part; falls back to text/html with tags stripped.</summary>
    internal static string? ExtractBody(MessagePart? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var parts = Flatten(payload).ToList();
        var plain = parts.FirstOrDefault(p => IsMime(p, "text/plain") && p.Body?.Data is not null);
        if (plain is not null)
        {
            return DecodeBase64Url(plain.Body.Data);
        }

        var html = parts.FirstOrDefault(p => IsMime(p, "text/html") && p.Body?.Data is not null);
        return html is null ? null : HtmlToText(DecodeBase64Url(html.Body.Data));
    }

    private static IEnumerable<MessagePart> Flatten(MessagePart part)
    {
        yield return part;
        foreach (var child in part.Parts ?? [])
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    private static bool IsMime(MessagePart part, string mime)
        => part.MimeType?.StartsWith(mime, StringComparison.OrdinalIgnoreCase) == true;

    internal static string DecodeBase64Url(string data)
    {
        var s = data.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    internal static string HtmlToText(string html)
    {
        var withoutBlocks = ScriptOrStyle().Replace(html, " ");
        var text = WebUtility.HtmlDecode(Tags().Replace(withoutBlocks, " "));
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..Math.Max(0, max)];

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
