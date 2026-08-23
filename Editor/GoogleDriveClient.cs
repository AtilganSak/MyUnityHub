using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MyUnityHub
{
    [Serializable]
    public class DriveFile
    {
        public string id;
        public string name;
        public string mimeType;
        public bool IsFolder => mimeType == "application/vnd.google-apps.folder";
    }

    [Serializable]
    class DriveFileList { public DriveFile[] files; public string nextPageToken; }

    [Serializable]
    class TokenResponse { public string access_token; public string refresh_token; public int expires_in; }

    /// <summary>
    /// Google Drive v3 over plain HttpClient. OAuth2 loopback (PKCE) for a personal
    /// Drive account; refresh token round-trips through the .myhub credential file so
    /// login is one-time.
    /// </summary>
    internal class GoogleDriveClient
    {
        const string Auth = "https://accounts.google.com/o/oauth2/v2/auth";
        const string Token = "https://oauth2.googleapis.com/token";
        const string Api = "https://www.googleapis.com/drive/v3";
        const string Upload = "https://www.googleapis.com/upload/drive/v3";
        // Least privilege: readonly covers listing/downloading folders the user already
        // owns; drive.file covers files/folders this tool creates (uploads). Full "drive"
        // is not needed. Existing refresh tokens keep their old scope until re-login.
        const string Scope = "https://www.googleapis.com/auth/drive.readonly " +
                             "https://www.googleapis.com/auth/drive.file";
        const string FolderMime = "application/vnd.google-apps.folder";
        // How long the loopback listener waits for the browser to come back.
        static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(2);

        static readonly HttpClient Http = new HttpClient();

        readonly string _clientId;
        readonly string _clientSecret;
        string _accessToken;
        string _refreshToken;
        DateTime _accessExpiry = DateTime.MinValue;

        /// <summary>Secrets come from the attached .myhub file, never from EditorPrefs.</summary>
        public GoogleDriveClient(Credentials creds)
        {
            _clientId = creds.ClientId;
            _clientSecret = creds.ClientSecret;
            _refreshToken = creds.RefreshToken;
        }

        public bool HasSavedLogin => !string.IsNullOrEmpty(_refreshToken);

        public void SignOut()
        {
            _refreshToken = "";
            _accessToken = null;
            CredentialStore.SetRefreshToken("");
        }

        /// <summary>Explicit login button entry point: runs/refreshes OAuth now.</summary>
        public Task Login() => EnsureToken();

        // ---- auth ----------------------------------------------------------

        bool TokenValid => !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessExpiry;

        // Serialised: two concurrent callers would both miss the cached token and each
        // start their own browser login.
        readonly SemaphoreSlim _authGate = new SemaphoreSlim(1, 1);

        async Task EnsureToken()
        {
            if (TokenValid) return;
            await _authGate.WaitAsync();
            try
            {
                if (TokenValid) return; // someone else logged in while we waited

                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    if (await TryRefresh(_refreshToken)) return;
                    _refreshToken = ""; // stale/revoked -> full login
                    CredentialStore.SetRefreshToken("");
                }
                await LoopbackLogin();
            }
            finally { _authGate.Release(); }
        }

        async Task<bool> TryRefresh(string refresh)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            };
            using var resp = await Http.PostAsync(Token, new FormUrlEncodedContent(form));
            if (!resp.IsSuccessStatusCode) return false;
            var tok = JsonUtility.FromJson<TokenResponse>(await resp.Content.ReadAsStringAsync());
            ApplyToken(tok);
            return true;
        }

        async Task LoopbackLogin()
        {
            // Google "Desktop app" clients permit a loopback redirect on any port.
            int port = FreePort();
            string redirect = $"http://localhost:{port}/";

            string verifier = RandUrl(64);
            using var sha = SHA256.Create();
            string challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = RandUrl(16);

            string url = $"{Auth}?response_type=code&client_id={Uri.EscapeDataString(_clientId)}" +
                         $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scope)}" +
                         $"&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                         "&access_type=offline&prompt=consent";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
            // OpenURL is a Unity API: main thread only. Auth can be driven from a
            // background task (ListFolder etc. call EnsureToken off-thread).
            EditorDispatcher.Enqueue(() => Application.OpenURL(url));

            // Without a deadline the editor waits forever if the user closes the browser
            // tab instead of finishing the consent screen.
            var pending = listener.GetContextAsync();
            if (await Task.WhenAny(pending, Task.Delay(LoginTimeout)) != pending)
            {
                Observe(pending); // disposing the listener below makes it throw
                throw new TimeoutException(
                    $"Google sign-in was not completed within {LoginTimeout.TotalMinutes:0} minutes.");
            }

            var ctx = await pending;
            string code = ctx.Request.QueryString["code"];
            string gotState = ctx.Request.QueryString["state"];
            byte[] body = Encoding.UTF8.GetBytes(
                "<html><body><h3>Drive connected. You can close this tab.</h3></body></html>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
            listener.Stop();

            if (gotState != state) throw new Exception("OAuth state mismatch (CSRF).");
            if (string.IsNullOrEmpty(code)) throw new Exception("No OAuth code in the callback.");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirect,
            };
            using var resp = await Http.PostAsync(Token, new FormUrlEncodedContent(form));
            string tokenBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {tokenBody}");
            var tok = JsonUtility.FromJson<TokenResponse>(tokenBody);
            ApplyToken(tok);
            if (!string.IsNullOrEmpty(tok.refresh_token))
            {
                _refreshToken = tok.refresh_token;
                CredentialStore.SetRefreshToken(tok.refresh_token); // back into the .myhub file
            }
        }

        void ApplyToken(TokenResponse tok)
        {
            _accessToken = tok.access_token;
            _accessExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, tok.expires_in - 60));
        }

        HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return r;
        }

        /// <summary>Send + surface the API error body (Drive packs the real reason there).</summary>
        static async Task<string> SendChecked(HttpRequestMessage req)
        {
            using (req)
            using (var resp = await Http.SendAsync(req))
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
                return body;
            }
        }

        // ---- listing -------------------------------------------------------

        public async Task<List<DriveFile>> ListFolder(string folderId)
        {
            await EnsureToken();
            var all = new List<DriveFile>();
            string pageToken = null;
            do
            {
                string q = Uri.EscapeDataString($"'{folderId}' in parents and trashed=false");
                string url = $"{Api}/files?q={q}&pageSize=200&orderBy=folder,name" +
                             "&fields=nextPageToken,files(id,name,mimeType)" +
                             "&supportsAllDrives=true&includeItemsFromAllDrives=true";
                if (pageToken != null) url += "&pageToken=" + pageToken;

                var list = JsonUtility.FromJson<DriveFileList>(await SendChecked(Req(HttpMethod.Get, url)));
                if (list.files != null) all.AddRange(list.files);
                pageToken = list.nextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
            return all;
        }

        // ---- download ------------------------------------------------------

        public async Task DownloadFile(string id, string destPath)
        {
            await EnsureToken();
            using var req = Req(HttpMethod.Get, $"{Api}/files/{id}?alt=media&supportsAllDrives=true");
            // ResponseHeadersRead + stream copy: never hold a whole asset in memory.
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = File.Create(destPath);
            await src.CopyToAsync(dst);
        }

        /// <summary>Recursively download a Drive folder into destDir/folderName.</summary>
        public async Task DownloadFolder(DriveFile folder, string destDir)
        {
            string target = Path.Combine(destDir, folder.name);
            Directory.CreateDirectory(target);
            foreach (var child in await ListFolder(folder.id))
            {
                if (child.IsFolder) await DownloadFolder(child, target);
                else await DownloadFile(child.id, Path.Combine(target, child.name));
            }
        }

        // ---- upload --------------------------------------------------------

        public async Task UploadFile(string localPath, string parentId)
        {
            await EnsureToken();
            string meta = "{\"name\":\"" + Path.GetFileName(localPath).Replace("\"", "\\\"") +
                          "\",\"parents\":[\"" + parentId + "\"]}";

            var content = new MultipartContent("related");
            var metaPart = new StringContent(meta, Encoding.UTF8, "application/json");
            content.Add(metaPart);
            // Stream instead of buffering: a large asset must not be pulled into memory
            // whole. SendChecked disposes the request, which disposes this stream.
            var filePart = new StreamContent(File.OpenRead(localPath));
            filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(filePart);

            var req = Req(HttpMethod.Post, $"{Upload}/files?uploadType=multipart&supportsAllDrives=true");
            req.Content = content;
            await SendChecked(req);
        }

        public async Task UploadFolder(string localDir, string parentId)
        {
            string name = new DirectoryInfo(localDir).Name;
            string folderId = await CreateFolder(name, parentId);
            foreach (var f in Directory.GetFiles(localDir))
                if (!f.EndsWith(".meta")) await UploadFile(f, folderId); // skip Unity .meta noise
            foreach (var d in Directory.GetDirectories(localDir))
                await UploadFolder(d, folderId);
        }

        async Task<string> CreateFolder(string name, string parentId)
        {
            await EnsureToken();
            string meta = "{\"name\":\"" + name.Replace("\"", "\\\"") +
                          "\",\"mimeType\":\"" + FolderMime + "\",\"parents\":[\"" + parentId + "\"]}";
            var req = Req(HttpMethod.Post, $"{Api}/files?supportsAllDrives=true");
            req.Content = new StringContent(meta, Encoding.UTF8, "application/json");
            return JsonUtility.FromJson<DriveFile>(await SendChecked(req)).id;
        }

        // ---- helpers -------------------------------------------------------

        static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string RandUrl(int bytes)
        {
            var b = new byte[bytes];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(b);
            return Base64Url(b);
        }

        static string Base64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Swallow the failure of a task we stopped waiting for, so it cannot
        /// resurface later as an unobserved exception.</summary>
        static void Observe(Task t) =>
            t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
}

