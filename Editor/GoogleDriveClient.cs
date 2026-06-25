using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MyUnityHub
{
    [Serializable]
    public class DriveFile
    {
        public string id;
        public string name;
        public string mimeType;
        public string size;
        public bool IsFolder => mimeType == "application/vnd.google-apps.folder";
    }

    [Serializable]
    class DriveFileList { public DriveFile[] files; public string nextPageToken; }

    [Serializable]
    class TokenResponse { public string access_token; public string refresh_token; public int expires_in; }

    /// <summary>
    /// Google Drive v3 over plain HttpClient. OAuth2 loopback (PKCE) for a personal
    /// Drive account; refresh token cached in EditorPrefs so login is one-time.
    /// </summary>
    internal class GoogleDriveClient
    {
        const string Auth = "https://accounts.google.com/o/oauth2/v2/auth";
        const string Token = "https://oauth2.googleapis.com/token";
        const string Api = "https://www.googleapis.com/drive/v3";
        const string Upload = "https://www.googleapis.com/upload/drive/v3";
        const string Scope = "https://www.googleapis.com/auth/drive";
        const string FolderMime = "application/vnd.google-apps.folder";

        const string RefreshKey = "DriveGitHubHub.refresh_token";

        static readonly HttpClient Http = new HttpClient();

        readonly string _clientId;
        readonly string _clientSecret;
        string _accessToken;
        string _refreshToken;
        DateTime _accessExpiry = DateTime.MinValue;

        public GoogleDriveClient(string clientId, string clientSecret)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _refreshToken = EditorPrefs.GetString(RefreshKey, ""); // ctor runs on main thread (UI)
        }

        public bool HasSavedLogin => !string.IsNullOrEmpty(_refreshToken);

        public void SignOut()
        {
            _refreshToken = "";
            _accessToken = null;
            EditorPrefs.DeleteKey(RefreshKey);
        }

        /// <summary>Explicit login button entry point: runs/refreshes OAuth now.</summary>
        public Task Login() => EnsureToken();

        // EditorPrefs is main-thread only; marshal writes (we run inside Task.Run).
        static void PersistRefresh(string token) =>
            EditorDispatcher.Enqueue(() => EditorPrefs.SetString(RefreshKey, token));

        // ---- auth ----------------------------------------------------------

        async Task EnsureToken()
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessExpiry) return;

            if (!string.IsNullOrEmpty(_refreshToken))
            {
                if (await TryRefresh(_refreshToken)) return;
                _refreshToken = ""; // stale/revoked -> full login
                EditorDispatcher.Enqueue(() => EditorPrefs.DeleteKey(RefreshKey));
            }
            await LoopbackLogin();
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
            var resp = await Http.PostAsync(Token, new FormUrlEncodedContent(form));
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
            string challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = RandUrl(16);

            string url = $"{Auth}?response_type=code&client_id={Uri.EscapeDataString(_clientId)}" +
                         $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scope)}" +
                         $"&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
                         "&access_type=offline&prompt=consent";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
            Application.OpenURL(url);

            var ctx = await listener.GetContextAsync();
            string code = ctx.Request.QueryString["code"];
            string gotState = ctx.Request.QueryString["state"];
            byte[] body = Encoding.UTF8.GetBytes(
                "<html><body><h3>Drive baglandi. Bu sekmeyi kapatabilirsin.</h3></body></html>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.OutputStream.Close();
            listener.Stop();

            if (gotState != state) throw new Exception("OAuth state mismatch (CSRF).");
            if (string.IsNullOrEmpty(code)) throw new Exception("OAuth code alinamadi.");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirect,
            };
            var resp = await Http.PostAsync(Token, new FormUrlEncodedContent(form));
            resp.EnsureSuccessStatusCode();
            var tok = JsonUtility.FromJson<TokenResponse>(await resp.Content.ReadAsStringAsync());
            ApplyToken(tok);
            if (!string.IsNullOrEmpty(tok.refresh_token))
            {
                _refreshToken = tok.refresh_token;
                PersistRefresh(tok.refresh_token);
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
            var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            return body;
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
                             "&fields=nextPageToken,files(id,name,mimeType,size)" +
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
            var resp = await Http.SendAsync(Req(HttpMethod.Get, $"{Api}/files/{id}?alt=media&supportsAllDrives=true"));
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            File.WriteAllBytes(destPath, bytes);
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
            var filePart = new ByteArrayContent(File.ReadAllBytes(localPath));
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
    }
}

