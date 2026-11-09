using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Mindows;

/// <summary>
/// Per-app notification counts from Windows' own notification database: the badge a packaged app sets,
/// else the toasts still waiting in the Action Center. Keyed like the dock's slots, so a lookup needs no mapping.
/// </summary>
static class NotificationBadges
{
    // shortcut: wpndatabase.db is undocumented and can change in a Windows update; the counts then stay empty and the dock keeps the window-title count. Upgrade to UserNotificationListener if Mindows ever gets a package identity.
    // shortcut: handlers named by a known-folder path ({GUID}/Vendor/App.exe, e.g. KakaoTalk) are not matched to their slot; no badge for those.
    static readonly string Db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");
    // Toast text is private message content, so it is never selected; only a badge's payload is read.
    const string Sql = "select h.PrimaryId, n.Type, n.ExpiryTime, case when n.Type = 'badge' then n.Payload end " +
                       "from Notification n join NotificationHandler h on n.HandlerId = h.RecordId where n.Type in ('toast', 'badge')";
    static readonly Regex BadgeValueRx = new(@"value=""([^""]*)""");

    static Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    static long stamp, readAt;

    static NotificationBadges()
    {
        Debug.Assert(BadgeValue("<badge value=\"3\"/>") == 3 && BadgeValue("<badge value=\"0\"/>") == 0
            && BadgeValue("<badge value=\"none\"/>") == 0 && BadgeValue("<badge value=\"newMessage\"/>") == -1);
    }

    /// <summary>Count by slot key: a number, -1 for a glyph badge (shown as a plain dot), 0 for a badge that was cleared. Apps without any row are absent.</summary>
    public static Dictionary<string, int> Read()
    {
        // Badges change by writing to the database, so its files' times say when to read again; toasts also expire, hence the minute.
        var now = Stopwatch.GetTimestamp();
        var changed = File.GetLastWriteTimeUtc(Db).Ticks ^ File.GetLastWriteTimeUtc(Db + "-wal").Ticks;
        if (changed == stamp && Stopwatch.GetElapsedTime(readAt, now).TotalSeconds < 60) return counts;
        try
        {
            counts = Query();
            (stamp, readAt) = (changed, now);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { } // keep the last counts
        return counts;
    }

    static Dictionary<string, int> Query()
    {
        var badges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toasts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToFileTimeUtc();
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Db, out db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0) throw new InvalidOperationException("cannot open " + Db);
            sqlite3_busy_timeout(db, 50); // Refresh runs on the UI thread; a locked database is skipped, not waited for
            if (sqlite3_prepare_v2(db, Sql, -1, out st, IntPtr.Zero) != 0) throw new InvalidOperationException("unexpected notification schema");
            int rc;
            while ((rc = sqlite3_step(st)) == 100 /* SQLITE_ROW */)
            {
                var id = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) ?? "";
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(st, 1)) == "badge")
                    badges[id] = BadgeValue(Marshal.PtrToStringUTF8(sqlite3_column_text(st, 3)));
                else if (sqlite3_column_int64(st, 2) > now) toasts[id] = toasts.GetValueOrDefault(id) + 1;
            }
            if (rc != 101 /* SQLITE_DONE */) throw new InvalidOperationException("notification database busy");
        }
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }

        // A badge row wins even at 0 (cleared); toasts count only for apps that set none.
        var byApp = new Dictionary<string, int>(toasts, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, n) in badges) byApp[id] = n;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, n) in byApp)
        {
            result[Config.AppsFolder + id] = n;       // packaged apps and apps with an explicit AppUserModelID
            if (id.Contains('/')) result[id.Replace('/', '\\')] = n; // classic apps named by their exe path
        }
        return result;
    }

    /// <summary>A badge's number, -1 for one of the glyph names (alert, newMessage, ...), 0 for none.</summary>
    static int BadgeValue(string? xml)
    {
        var v = xml == null ? "" : BadgeValueRx.Match(xml).Groups[1].Value;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : v is "" or "none" ? 0 : -1;
    }

    const string Lib = "winsqlite3.dll"; // ships with Windows 10 and later
    [DllImport(Lib)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr st, IntPtr tail);
    [DllImport(Lib)] static extern int sqlite3_step(IntPtr st);
    [DllImport(Lib)] static extern IntPtr sqlite3_column_text(IntPtr st, int col);
    [DllImport(Lib)] static extern long sqlite3_column_int64(IntPtr st, int col);
    [DllImport(Lib)] static extern int sqlite3_finalize(IntPtr st);
    [DllImport(Lib)] static extern int sqlite3_close(IntPtr db);
}
