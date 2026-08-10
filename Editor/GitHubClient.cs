using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MyUnityHub
{
    [Serializable]
    public class Repo
    {
        public string name;
        public string full_name;
        public string clone_url;
        public string html_url;
        public string default_branch;
        public bool isPackage; // has package.json at root (set after probe)

        public string GitUrl => clone_url; // UPM accepts the https .git url
    }

    [Serializable]
    class RepoArrayWrap { public Repo[] items; }

    /// <summary>GitHub REST v3 with a PAT. Lists repos and flags UPM packages.</summary>
    internal class GitHubClient
    {
        static readonly HttpClient Http = MakeHttp();

        readonly string _pat;
        public GitHubClient(string pat) { _pat = pat; }

        static HttpClient MakeHttp()
        {
            var h = new HttpClient();
            h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DriveGitHubHub", "1.0"));
            h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return h;
        }

        HttpRequestMessage Req(HttpMethod m, string url)
        {
            var r = new HttpRequestMessage(m, url);
            if (!string.IsNullOrEmpty(_pat))
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _pat);
            return r;
        }

        /// <summary>Send + surface the API error body (GitHub explains rate limits there).</summary>
        async Task<string> SendChecked(string url)
        {
            using (var req = Req(HttpMethod.Get, url))
            using (var resp = await Http.SendAsync(req))
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
                return body;
            }
        }

        public async Task<List<Repo>> ListRepos()
        {
            var all = new List<Repo>();
            for (int page = 1; page <= 10; page++) // cap 1000 repos
            {
                string url = $"https://api.github.com/user/repos?per_page=100&page={page}&sort=updated";
                string json = await SendChecked(url);
                var wrap = JsonUtility.FromJson<RepoArrayWrap>("{\"items\":" + json + "}");
                if (wrap.items == null || wrap.items.Length == 0) break;
                all.AddRange(wrap.items);
                if (wrap.items.Length < 100) break;
            }
            return all;
        }

        /// <summary>
        /// Probe every repo for a root package.json, max 8 in flight. Unbounded
        /// parallelism trips GitHub's secondary rate limit, which comes back as 403
        /// and would otherwise be misread as "not a package".
        /// </summary>
        public async Task ProbeIsPackages(IEnumerable<Repo> repos)
        {
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(repos.Select(async r =>
            {
                await gate.WaitAsync();
                try { await ProbeIsPackage(r); }
                finally { gate.Release(); }
            }));
        }

        /// <summary>GET package.json at repo root. 200 => Unity package, 404 => not.
        /// Anything else (403 rate limit, 401 bad PAT) throws instead of silently
        /// flagging the repo as "no package".</summary>
        public async Task ProbeIsPackage(Repo r)
        {
            string url = $"https://api.github.com/repos/{r.full_name}/contents/package.json";
            using (var req = Req(HttpMethod.Get, url))
            using (var resp = await Http.SendAsync(req))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) { r.isPackage = false; return; }
                if (!resp.IsSuccessStatusCode)
                    throw new Exception(
                        $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {await resp.Content.ReadAsStringAsync()}");
                r.isPackage = true;
            }
        }
    }
}

