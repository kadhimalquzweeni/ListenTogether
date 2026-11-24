using System.Diagnostics;
using System.Security.Cryptography;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Server.Sessions;

/// <summary>
/// In-memory, single-instance session registry. Audio payloads are never stored here.
/// </summary>
public sealed class SessionRegistry
{
    private const int MaximumParticipants = 16;
    private const int MaximumDisplayNameLength = 32;
    private const int MaximumOpusPacketBytes = 1_275;
    private static readonly char[] CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly object _sync = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connectionSessions = new(StringComparer.Ordinal);

    public SessionInvitation CreateSession(string connectionId, string displayName)
    {
        ValidateConnectionId(connectionId);
        displayName = ValidateDisplayName(displayName);

        lock (_sync)
        {
            if (_connectionSessions.ContainsKey(connectionId))
                throw new InvalidOperationException("This connection is already in a session.");

            string code;
            do { code = GenerateCode(); }
            while (_sessions.ContainsKey(code));

            var state = new SessionState(code, connectionId, displayName);
            _sessions.Add(code, state);
            _connectionSessions.Add(connectionId, code);
            return new SessionInvitation(code);
        }
    }

    public SessionJoinResult JoinSession(string connectionId, string code, string displayName)
    {
        ValidateConnectionId(connectionId);
        displayName = ValidateDisplayName(displayName);
        code = NormalizeCode(code);

        lock (_sync)
        {
            if (_connectionSessions.ContainsKey(connectionId))
                throw new InvalidOperationException("This connection is already in a session.");

            if (!_sessions.TryGetValue(code, out var session))
                throw new KeyNotFoundException("That listening session doesn't exist or has already ended.");

            if (session.Participants.Count >= MaximumParticipants)
                throw new InvalidOperationException("This session has reached its participant limit.");

            var participant = new SessionParticipant(displayName, false);
            session.Participants.Add(connectionId, participant);
            _connectionSessions.Add(connectionId, code);

            return new SessionJoinResult(
                new SessionInvitation(code),
                session.HostDisplayName,
                Array.AsReadOnly(session.Participants.Values.ToArray()),
                participant);
        }
    }

    public bool IsHost(string connectionId)
    {
        lock (_sync)
        {
            return _connectionSessions.TryGetValue(connectionId, out var code) &&
                _sessions.TryGetValue(code, out var session) &&
                session.HostConnectionId == connectionId;
        }
    }

    public bool TryGetSessionCode(string connectionId, out string code)
    {
        lock (_sync)
            return _connectionSessions.TryGetValue(connectionId, out code!);
    }

    public bool TryAcceptAudioPacket(string connectionId, long sequence, int packetLength)
    {
        if (packetLength is < 1 or > MaximumOpusPacketBytes || sequence < 0)
            return false;

        lock (_sync)
        {
            if (!_connectionSessions.TryGetValue(connectionId, out var code) ||
                !_sessions.TryGetValue(code, out var session) ||
                session.HostConnectionId != connectionId ||
                sequence <= session.LastSequence)
            {
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            if (session.LastPacketTimestamp != 0 &&
                Stopwatch.GetElapsedTime(session.LastPacketTimestamp, now) < TimeSpan.FromMilliseconds(10))
            {
                return false;
            }

            session.LastSequence = sequence;
            session.LastPacketTimestamp = now;
            return true;
        }
    }

    public bool TryAcceptAudioPacketBatch(
        string connectionId,
        IReadOnlyList<long> sequences,
        IReadOnlyList<int> packetLengths)
    {
        if (sequences is null || packetLengths is null ||
            sequences.Count is < 1 or > 5 || sequences.Count != packetLengths.Count)
            return false;

        for (var index = 0; index < sequences.Count; index++)
        {
            if (packetLengths[index] is < 1 or > MaximumOpusPacketBytes ||
                sequences[index] < 0 ||
                (index > 0 && sequences[index] <= sequences[index - 1]))
                return false;
        }

        lock (_sync)
        {
            if (!_connectionSessions.TryGetValue(connectionId, out var code) ||
                !_sessions.TryGetValue(code, out var session) ||
                session.HostConnectionId != connectionId ||
                sequences[0] <= session.LastSequence)
                return false;

            var now = Stopwatch.GetTimestamp();
            if (session.LastPacketTimestamp != 0 &&
                Stopwatch.GetElapsedTime(session.LastPacketTimestamp, now) < TimeSpan.FromMilliseconds(10))
                return false;

            session.LastSequence = sequences[^1];
            session.LastPacketTimestamp = now;
            return true;
        }
    }

    public SessionDeparture? RemoveConnection(string connectionId)
    {
        lock (_sync)
        {
            if (!_connectionSessions.Remove(connectionId, out var code) ||
                !_sessions.TryGetValue(code, out var session) ||
                !session.Participants.Remove(connectionId, out var participant))
            {
                return null;
            }

            var isHost = connectionId == session.HostConnectionId;
            if (isHost)
            {
                _sessions.Remove(code);
                foreach (var remainingConnection in session.Participants.Keys)
                    _connectionSessions.Remove(remainingConnection);
            }

            return new SessionDeparture(code, participant, isHost);
        }
    }

    public int SessionCount
    {
        get { lock (_sync) return _sessions.Count; }
    }

    private static string GenerateCode()
    {
        Span<char> code = stackalloc char[8];
        for (var index = 0; index < code.Length; index++)
            code[index] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(code);
    }

    private static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A session code is required.", nameof(code));

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 8)
            throw new ArgumentException("Session codes must contain eight characters.", nameof(code));
        if (normalized.Any(character => !CodeAlphabet.Contains(character)))
            throw new ArgumentException("The session code contains invalid characters.", nameof(code));

        return normalized;
    }

    private static string ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));

        var normalized = displayName.Trim();
        if (normalized.Length > MaximumDisplayNameLength)
            throw new ArgumentException($"Display names must be {MaximumDisplayNameLength} characters or fewer.", nameof(displayName));

        return normalized;
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A SignalR connection ID is required.", nameof(connectionId));
    }

    private sealed class SessionState(string code, string hostConnectionId, string hostDisplayName)
    {
        public string Code { get; } = code;
        public string HostConnectionId { get; } = hostConnectionId;
        public string HostDisplayName { get; } = hostDisplayName;
        public Dictionary<string, SessionParticipant> Participants { get; } =
            new(StringComparer.Ordinal) { [hostConnectionId] = new SessionParticipant(hostDisplayName, true) };
        public long LastSequence { get; set; } = -1;
        public long LastPacketTimestamp { get; set; }
    }
}
