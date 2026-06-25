using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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

        public async Task<List<Repo>> ListRepos()
        {
            var all = new List<Repo>();
            for (int page = 1; page <= 10; page++) // cap 1000 repos
            {
                string url = $"https://api.github.com/user/repos?per_page=100&page={page}&sort=updated";
                var resp = await Http.SendAsync(Req(HttpMethod.Get, url));
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync();
                var wrap = JsonUtility.FromJson<RepoArrayWrap>("{\"items\":" + json + "}");
                if (wrap.items == null || wrap.items.Length == 0) break;
                all.AddRange(wrap.items);
                if (wrap.items.Length < 100) break;
            }
            return all;
        }

        /// <summary>HEAD-ish probe for package.json at repo root. 200 => Unity package.</summary>
        public async Task ProbeIsPackage(Repo r)
        {
            string url = $"https://api.github.com/repos/{r.full_name}/contents/package.json";
            var resp = await Http.SendAsync(Req(HttpMethod.Get, url));
            r.isPackage = resp.IsSuccessStatusCode;
        }
    }
}

