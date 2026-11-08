// BeePresence: a minimal MusicBee -> Discord "Listening to" presence plugin.
// No third-party libraries. It talks to the Discord desktop client over its local
// IPC named pipe and gets cover art URLs from the public iTunes Search API, so
// nothing is uploaded anywhere.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MusicBeePlugin
{
  public partial class Plugin
  {
    private MusicBeeApiInterface mbApi;
    private readonly PluginInfo about = new PluginInfo();

    private string clientId = "910864681588105236"; // DiscordBee's app ("MusicBee"); override in BeePresence.ini
    private bool showWhenPaused = false;
    private bool coverArt = true;
    private bool useLastfm = true, useDeezer = true, useItunes = true;
    private string lastfmKey = "";
    private string logPath, iniPath;

    private Thread worker;
    private volatile bool running;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private Track desired;              // what Discord should show (null = nothing)
    private readonly object stateLock = new object();

    private DiscordIpc ipc;
    private string lastSentJson;
    private DateTime lastSendUtc = DateTime.MinValue;

    private static readonly HttpClient http = CreateHttp();
    private readonly ConcurrentDictionary<string, string> artCache = new ConcurrentDictionary<string, string>();

    private sealed class Track
    {
      public string Title, Artist, Album;
      public bool Paused;
      public long StartMs, EndMs; // unix ms, 0 = unknown
    }

    public PluginInfo Initialise(IntPtr apiInterfacePtr)
    {
      mbApi = new MusicBeeApiInterface();
      mbApi.Initialise(apiInterfacePtr);
      about.PluginInfoVersion = PluginInfoVersion;
      about.Name = "BeePresence";
      about.Description = "Shows what you're listening to on Discord (lightweight DiscordBee replacement)";
      about.Author = "Toasty";
      about.TargetApplication = "";
      about.Type = PluginType.General;
      about.VersionMajor = 1;
      about.VersionMinor = 2;
      about.Revision = 0;
      about.MinInterfaceVersion = MinInterfaceVersion;
      about.MinApiRevision = MinApiRevision;
      about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
      about.ConfigurationPanelHeight = PanelHeight();

      string dir = mbApi.Setting_GetPersistentStoragePath();
      iniPath = Path.Combine(dir, "BeePresence.ini");
      logPath = Path.Combine(dir, "BeePresence.log");
      LoadSettings();

      running = true;
      worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BeePresence" };
      worker.Start();
      return about;
    }

    // ---- Settings panel (shown inline in MusicBee's Preferences > Plugins) ----
    private TextBox idBox;
    private CheckBox pausedBox, artBox, lastfmBox, deezerBox, itunesBox;
    private TextBox keyBox;
    private volatile bool reconnect;

    private static int PanelHeight()
    {
      int line = TextRenderer.MeasureText("Ag", SystemFonts.MessageBoxFont).Height;
      return line * 7 + 10;
    }

    public bool Configure(IntPtr panelHandle)
    {
      LoadSettings();
      if (panelHandle == IntPtr.Zero) return false;
      var panel = (Panel)Control.FromHandle(panelHandle);
      var font = panel.Font;
      int gap = font.Height / 2, y = 0;

      var idLabel = new Label { Text = "Discord Application ID:", AutoSize = true };
      idBox = new TextBox { Text = clientId, Width = TextRenderer.MeasureText("00000000000000000000000", font).Width };
      var link = new LinkLabel { Text = "Create your own…", AutoSize = true };
      link.LinkClicked += (s, e) => { try { Process.Start("https://discord.com/developers/applications"); } catch { } };
      idLabel.Location = new Point(0, y + (idBox.PreferredHeight - idLabel.PreferredHeight) / 2);
      idBox.Location = new Point(idLabel.PreferredWidth + gap, y);
      link.Location = new Point(idBox.Right + gap, idLabel.Top);
      y += idBox.PreferredHeight + 2;

      var hint = new Label { Text = "Discord shows this application's name as \"Listening to <name>\".", AutoSize = true, Location = new Point(0, y) };
      y += hint.PreferredHeight + gap;

      pausedBox = new CheckBox { Text = "Keep showing the song while paused", AutoSize = true, Checked = showWhenPaused, Location = new Point(0, y) };
      y += pausedBox.PreferredSize.Height + 2;
      artBox = new CheckBox { Text = "Show album cover from:", AutoSize = true, Checked = coverArt, Location = new Point(0, y) };
      int x = artBox.PreferredSize.Width + gap;
      lastfmBox = new CheckBox { Text = "Last.fm", AutoSize = true, Checked = useLastfm, Location = new Point(x, y) };
      x += lastfmBox.PreferredSize.Width + gap;
      deezerBox = new CheckBox { Text = "Deezer", AutoSize = true, Checked = useDeezer, Location = new Point(x, y) };
      x += deezerBox.PreferredSize.Width + gap;
      itunesBox = new CheckBox { Text = "iTunes", AutoSize = true, Checked = useItunes, Location = new Point(x, y) };
      y += artBox.PreferredSize.Height + 2;

      int indent = font.Height + 2;
      var keyLabel = new Label { Text = "Last.fm API key:", AutoSize = true };
      keyBox = new TextBox { Text = lastfmKey, Width = TextRenderer.MeasureText("00000000000000000000000000000000000", font).Width };
      var keyLink = new LinkLabel { Text = "Get a free key…", AutoSize = true };
      keyLink.LinkClicked += (s, e) => { try { Process.Start("https://www.last.fm/api/account/create"); } catch { } };
      keyLabel.Location = new Point(indent, y + (keyBox.PreferredHeight - keyLabel.PreferredHeight) / 2);
      keyBox.Location = new Point(indent + keyLabel.PreferredWidth + gap, y);
      keyLink.Location = new Point(keyBox.Right + gap, keyLabel.Top);

      // Sub-options only matter when covers are on (and the key only when Last.fm is on).
      void Sync()
      {
        lastfmBox.Enabled = deezerBox.Enabled = itunesBox.Enabled = artBox.Checked;
        keyLabel.Enabled = keyBox.Enabled = keyLink.Enabled = artBox.Checked && lastfmBox.Checked;
      }
      artBox.CheckedChanged += (s, e) => Sync();
      lastfmBox.CheckedChanged += (s, e) => Sync();
      Sync();

      panel.Controls.AddRange(new Control[] { idLabel, idBox, link, hint, pausedBox, artBox, lastfmBox, deezerBox, itunesBox, keyLabel, keyBox, keyLink });
      return false;
    }

    // Called by MusicBee when the user clicks Save/Apply in Preferences. Takes effect immediately.
    public void SaveSettings()
    {
      if (idBox == null || idBox.IsDisposed) return;
      string id = idBox.Text.Trim();
      if (Regex.IsMatch(id, "^[0-9]{15,25}$"))
      {
        if (id != clientId) reconnect = true;
        clientId = id;
      }
      else
      {
        MessageBox.Show("That doesn't look like a Discord Application ID (it should be a long number). Keeping " + clientId + ".",
          "BeePresence", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        idBox.Text = clientId;
      }
      showWhenPaused = pausedBox.Checked;
      bool coversChanged = coverArt != artBox.Checked || useLastfm != lastfmBox.Checked || useDeezer != deezerBox.Checked ||
                           useItunes != itunesBox.Checked || lastfmKey != keyBox.Text.Trim();
      coverArt = artBox.Checked;
      useLastfm = lastfmBox.Checked;
      useDeezer = deezerBox.Checked;
      useItunes = itunesBox.Checked;
      lastfmKey = keyBox.Text.Trim();
      if (coversChanged) artCache.Clear(); // re-look-up with the new sources
      WriteSettings();
      Refresh();
    }

    public void Close(PluginCloseReason reason)
    {
      running = false;
      wake.Set();
      try { worker?.Join(1500); } catch { }
      try { ipc?.Dispose(); } catch { }
    }

    public void Uninstall()
    {
      try { File.Delete(iniPath); File.Delete(logPath); } catch { }
    }

    public void ReceiveNotification(string sourceFileUrl, NotificationType type)
    {
      switch (type)
      {
        case NotificationType.PluginStartup:
        case NotificationType.TrackChanged:
        case NotificationType.PlayStateChanged:
          Refresh();
          break;
      }
    }

    // Snapshot MusicBee's player state into `desired` and poke the worker.
    private void Refresh()
    {
      Track t = null;
      try
      {
        var state = mbApi.Player_GetPlayState();
        bool playing = state == PlayState.Playing;
        bool paused = state == PlayState.Paused;
        if ((playing || (paused && showWhenPaused)) && !string.IsNullOrEmpty(mbApi.NowPlaying_GetFileUrl()))
        {
          t = new Track
          {
            Title = Tag(MetaDataType.TrackTitle),
            Artist = Tag(MetaDataType.Artist),
            Album = Tag(MetaDataType.Album),
            Paused = paused,
          };
          if (string.IsNullOrEmpty(t.Title))
            t.Title = Path.GetFileNameWithoutExtension(mbApi.NowPlaying_GetFileUrl());
          if (playing)
          {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int pos = mbApi.Player_GetPosition();
            int dur = mbApi.NowPlaying_GetDuration();
            t.StartMs = now - Math.Max(0, pos);
            if (dur > 0) t.EndMs = t.StartMs + dur;
          }
        }
      }
      catch (Exception e) { Log("refresh: " + e.Message); }

      lock (stateLock) desired = t;
      wake.Set();
    }

    private string Tag(MetaDataType f) => (mbApi.NowPlaying_GetFileTag(f) ?? "").Trim();

    private void WorkerLoop()
    {
      while (running)
      {
        wake.WaitOne(5000);
        if (!running) break;
        try
        {
          // Coalesce bursts (fast skipping) and stay under Discord's 5-updates/20s limit.
          Thread.Sleep(300);
          var sinceLast = DateTime.UtcNow - lastSendUtc;
          if (sinceLast < TimeSpan.FromSeconds(4)) Thread.Sleep(TimeSpan.FromSeconds(4) - sinceLast);

          DetectSeek();

          Track t;
          lock (stateLock) t = desired;
          string json = BuildActivityJson(t);

          if (reconnect)
          {
            // Application ID changed: drop the old connection (Discord clears its presence).
            reconnect = false;
            try { ipc?.Dispose(); } catch { }
            ipc = null;
          }
          if (ipc == null || !ipc.Connected)
          {
            ipc?.Dispose();
            ipc = DiscordIpc.TryConnect(clientId, Log);
            lastSentJson = null;
            if (ipc == null) continue; // Discord not running; retry in 5s
          }
          if (json == lastSentJson) continue;

          ipc.SetActivity(json);
          lastSentJson = json;
          lastSendUtc = DateTime.UtcNow;
        }
        catch (Exception e)
        {
          Log("worker: " + e.Message);
          try { ipc?.Dispose(); } catch { }
          ipc = null;
        }
      }
      // Leave a clean status behind when MusicBee closes.
      try { ipc?.SetActivity("null"); } catch { }
    }

    // MusicBee doesn't raise an event on seek, so re-snapshot if the position has drifted.
    private void DetectSeek()
    {
      Track t;
      lock (stateLock) t = desired;
      if (t == null || t.Paused || t.StartMs == 0) return;
      try
      {
        if (mbApi.Player_GetPlayState() != PlayState.Playing) return;
        long expectedStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - mbApi.Player_GetPosition();
        if (Math.Abs(expectedStart - t.StartMs) > 2500) Refresh();
      }
      catch { }
    }

    private string BuildActivityJson(Track t)
    {
      if (t == null) return "null";
      var sb = new StringBuilder("{\"type\":2,\"status_display_type\":1");
      sb.Append(",\"details\":").Append(J(Field(t.Title)));
      sb.Append(",\"state\":").Append(J(Field(string.IsNullOrEmpty(t.Artist) ? "Unknown artist" : t.Artist) + (t.Paused ? " (paused)" : "")));
      if (t.StartMs > 0)
      {
        sb.Append(",\"timestamps\":{\"start\":").Append(t.StartMs);
        if (t.EndMs > 0) sb.Append(",\"end\":").Append(t.EndMs);
        sb.Append('}');
      }
      string art = coverArt ? GetCoverUrl(t) : null;
      if (art != null)
      {
        sb.Append(",\"assets\":{\"large_image\":").Append(J(art));
        if (!string.IsNullOrEmpty(t.Album)) sb.Append(",\"large_text\":").Append(J(Field(t.Album)));
        sb.Append('}');
      }
      sb.Append('}');
      return sb.ToString();
    }

    // Discord requires 2..128 chars for text fields (single-char titles are rejected).
    private static string Field(string s)
    {
      s = s ?? "";
      if (s.Length < 2) s = s.PadRight(2, '⠀'); // braille blank: invisible but counts as a char
      if (s.Length > 128) s = s.Substring(0, 127) + "…";
      return s;
    }

    // Find a public cover image URL. Discord accepts external https URLs as large_image
    // directly, so nothing needs to be uploaded. Sources are tried most-exact first:
    // Last.fm (exact artist+album lookup, needs a free API key) -> Deezer (fielded search)
    // -> iTunes (keyword search).
    private string GetCoverUrl(Track t)
    {
      string key = (t.Artist + "|" + (string.IsNullOrEmpty(t.Album) ? "#" + t.Title : t.Album)).ToLowerInvariant();
      if (artCache.TryGetValue(key, out var cached)) return cached;

      var sources = new System.Collections.Generic.List<(string name, Func<Track, string> find)>();
      if (useLastfm && !string.IsNullOrEmpty(lastfmKey)) sources.Add(("Last.fm", FindLastfm));
      if (useDeezer) sources.Add(("Deezer", FindDeezer));
      if (useItunes) sources.Add(("iTunes", FindItunes));

      bool anyError = false;
      foreach (var (name, find) in sources)
      {
        try
        {
          string url = find(t);
          if (url != null) { artCache[key] = url; return url; }
        }
        catch (Exception e) { anyError = true; Log("art (" + name + "): " + e.Message); }
      }
      if (!anyError) artCache[key] = null; // genuine miss everywhere: cache it so we don't hammer the APIs
      return null;
    }

    // Returns the body even for 4xx (Last.fm explains bad keys in the body); throws on
    // server errors/rate limits so the miss isn't cached.
    private static string Get(string url)
    {
      using (var r = http.GetAsync(url).GetAwaiter().GetResult())
      {
        int code = (int)r.StatusCode;
        if (code >= 500 || code == 429) throw new HttpRequestException("HTTP " + code);
        return r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
      }
    }
    private static string Unescape(string jsonString) => jsonString.Replace("\\/", "/");
    private static string Clean(string s) => (s ?? "").Replace("\"", "");

    private string FindLastfm(Track t)
    {
      string baseUrl = "https://ws.audioscrobbler.com/2.0/?format=json&autocorrect=1&api_key=" + Uri.EscapeDataString(lastfmKey) +
                       "&artist=" + Uri.EscapeDataString(t.Artist);
      string url = null;
      if (!string.IsNullOrEmpty(t.Album))
        url = LastfmImage(Get(baseUrl + "&method=album.getinfo&album=" + Uri.EscapeDataString(t.Album)));
      if (url == null) // track.getInfo carries the album's images too
        url = LastfmImage(Get(baseUrl + "&method=track.getinfo&track=" + Uri.EscapeDataString(t.Title)));
      return url;
    }

    private string LastfmImage(string body)
    {
      var err = Regex.Match(body, "\"error\"\\s*:\\s*(\\d+)");
      if (err.Success && (err.Groups[1].Value == "10" || err.Groups[1].Value == "26"))
        throw new Exception("invalid or suspended Last.fm API key");
      string best = null;
      foreach (Match m in Regex.Matches(body, "\"#text\"\\s*:\\s*\"(https://[^\"]+/i/u/[^\"]+)\""))
        best = Unescape(m.Groups[1].Value); // images are listed small -> mega; keep the largest
      // Last.fm's grey star placeholder means "no cover".
      if (best == null || best.Contains("2a96cbd8b46e442fc41c2b86b821562f")) return null;
      return Regex.Replace(best, "/i/u/[^/]+/", "/i/u/600x600/");
    }

    private string FindDeezer(Track t)
    {
      string url = null;
      if (!string.IsNullOrEmpty(t.Album))
      {
        string q = "artist:\"" + Clean(t.Artist) + "\" album:\"" + Clean(t.Album) + "\"";
        var m = Regex.Match(Get("https://api.deezer.com/search/album?limit=1&q=" + Uri.EscapeDataString(q)), "\"cover_xl\"\\s*:\\s*\"([^\"]+)\"");
        if (m.Success) url = Unescape(m.Groups[1].Value);
      }
      // Deezer's fielded track search misses some well-known songs; plain keywords catch those.
      foreach (string q in new[] { "artist:\"" + Clean(t.Artist) + "\" track:\"" + Clean(t.Title) + "\"", t.Artist + " " + t.Title })
      {
        if (url != null) break;
        var m = Regex.Match(Get("https://api.deezer.com/search?limit=1&q=" + Uri.EscapeDataString(q)), "\"cover_xl\"\\s*:\\s*\"([^\"]+)\"");
        if (m.Success) url = Unescape(m.Groups[1].Value);
      }
      return url;
    }

    private string FindItunes(Track t)
    {
      bool byAlbum = !string.IsNullOrEmpty(t.Album);
      string q = "https://itunes.apple.com/search?media=music&limit=1&entity=" + (byAlbum ? "album" : "song") +
                 "&term=" + Uri.EscapeDataString(t.Artist + " " + (byAlbum ? t.Album : t.Title));
      var m = Regex.Match(Get(q), "\"artworkUrl100\"\\s*:\\s*\"(https?://[^\"]+)\"");
      if (!m.Success && byAlbum)
      {
        // Album titles in local tags are often messy; retry with the track title.
        q = "https://itunes.apple.com/search?media=music&limit=1&entity=song&term=" + Uri.EscapeDataString(t.Artist + " " + t.Title);
        m = Regex.Match(Get(q), "\"artworkUrl100\"\\s*:\\s*\"(https?://[^\"]+)\"");
      }
      return m.Success ? m.Groups[1].Value.Replace("100x100bb", "512x512bb") : null;
    }

    private static string J(string s)
    {
      var sb = new StringBuilder("\"");
      foreach (char c in s)
      {
        switch (c)
        {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
            break;
        }
      }
      return sb.Append('"').ToString();
    }

    private static HttpClient CreateHttp()
    {
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      var h = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
      h.DefaultRequestHeaders.UserAgent.ParseAdd("BeePresence/1.0 (MusicBee plugin)");
      return h;
    }

    private void LoadSettings()
    {
      try
      {
        if (!File.Exists(iniPath)) { WriteSettings(); return; }
        foreach (var raw in File.ReadAllLines(iniPath))
        {
          var line = raw.Trim();
          if (line.StartsWith(";") || !line.Contains("=")) continue;
          var k = line.Substring(0, line.IndexOf('=')).Trim().ToLowerInvariant();
          var v = line.Substring(line.IndexOf('=') + 1).Trim();
          if (k == "client_id" && v.Length > 0) clientId = v;
          else if (k == "show_when_paused") showWhenPaused = v.Equals("true", StringComparison.OrdinalIgnoreCase);
          else if (k == "cover_art") coverArt = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
          else if (k == "cover_lastfm") useLastfm = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
          else if (k == "cover_deezer") useDeezer = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
          else if (k == "cover_itunes") useItunes = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
          else if (k == "lastfm_api_key") lastfmKey = v;
        }
      }
      catch (Exception e) { Log("settings: " + e.Message); }
    }

    private void WriteSettings()
    {
      try
      {
        File.WriteAllText(iniPath,
          "; BeePresence settings (normally edited in MusicBee: Preferences > Plugins > BeePresence).\r\n" +
          "client_id=" + clientId + "\r\n" +
          "show_when_paused=" + (showWhenPaused ? "true" : "false") + "\r\n" +
          "cover_art=" + (coverArt ? "true" : "false") + "\r\n" +
          "cover_lastfm=" + (useLastfm ? "true" : "false") + "\r\n" +
          "cover_deezer=" + (useDeezer ? "true" : "false") + "\r\n" +
          "cover_itunes=" + (useItunes ? "true" : "false") + "\r\n" +
          "lastfm_api_key=" + lastfmKey + "\r\n");
      }
      catch (Exception e) { Log("settings write: " + e.Message); }
    }

    private readonly object logLock = new object();
    private void Log(string msg)
    {
      try
      {
        lock (logLock)
        {
          var fi = new FileInfo(logPath);
          if (fi.Exists && fi.Length > 256 * 1024) fi.Delete(); // never let it grow like DiscordBee's did
          File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + msg + "\r\n");
        }
      }
      catch { }
    }
  }

  /// Minimal Discord RPC client over the local named pipe (\\.\pipe\discord-ipc-N).
  /// Frame = int32 opcode + int32 length (little endian) + UTF-8 JSON.
  internal sealed class DiscordIpc : IDisposable
  {
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private readonly NamedPipeClientStream pipe;
    private readonly Action<string> log;
    private readonly object writeLock = new object();
    private volatile bool connected = true;
    public bool Connected => connected && pipe.IsConnected;

    private DiscordIpc(NamedPipeClientStream p, Action<string> log) { pipe = p; this.log = log; }

    public static DiscordIpc TryConnect(string clientId, Action<string> log)
    {
      for (int i = 0; i < 10; i++)
      {
        var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
        // ^ Asynchronous (overlapped) is required: on a synchronous handle Windows serializes I/O,
        //   so the blocking read in ReadLoop would stall every write.
        try { p.Connect(150); }
        catch { p.Dispose(); continue; }

        var c = new DiscordIpc(p, log);
        try
        {
          c.Write(OpHandshake, "{\"v\":1,\"client_id\":\"" + clientId + "\"}");
          var (op, body) = c.Read();
          if (op == OpFrame && body.Contains("\"READY\""))
          {
            new Thread(c.ReadLoop) { IsBackground = true, Name = "BeePresence-ipc" }.Start();
            log("connected to discord-ipc-" + i);
            return c;
          }
          log("handshake rejected on discord-ipc-" + i + ": " + body);
        }
        catch (Exception e) { log("handshake failed: " + e.Message); }
        c.Dispose();
      }
      return null;
    }

    public void SetActivity(string activityJson)
    {
      string payload = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" +
        System.Diagnostics.Process.GetCurrentProcess().Id + ",\"activity\":" + activityJson +
        "},\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}";
      Write(OpFrame, payload);
    }

    // Drain responses so the pipe never fills, answer pings, and surface errors.
    private void ReadLoop()
    {
      try
      {
        while (connected)
        {
          var (op, body) = Read();
          if (op == OpPing) Write(OpPong, body);
          else if (op == OpClose) { log("discord closed pipe: " + body); break; }
          else if (body.Contains("\"evt\":\"ERROR\"")) log("discord error: " + body);
        }
      }
      catch (Exception) { }
      connected = false;
    }

    private void Write(int op, string json)
    {
      byte[] data = Encoding.UTF8.GetBytes(json);
      byte[] frame = new byte[8 + data.Length];
      BitConverter.GetBytes(op).CopyTo(frame, 0);
      BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
      data.CopyTo(frame, 8);
      lock (writeLock) { pipe.Write(frame, 0, frame.Length); pipe.Flush(); }
    }

    private (int op, string body) Read()
    {
      byte[] hdr = ReadExact(8);
      int op = BitConverter.ToInt32(hdr, 0), len = BitConverter.ToInt32(hdr, 4);
      if (len < 0 || len > 1 << 20) throw new IOException("bad frame length");
      return (op, Encoding.UTF8.GetString(ReadExact(len)));
    }

    private byte[] ReadExact(int n)
    {
      var buf = new byte[n];
      int got = 0;
      while (got < n)
      {
        int r = pipe.Read(buf, got, n - got);
        if (r <= 0) throw new EndOfStreamException();
        got += r;
      }
      return buf;
    }

    public void Dispose() { connected = false; try { pipe.Dispose(); } catch { } }
  }
}
