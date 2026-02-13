﻿﻿﻿using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Text.Json;



namespace Soapimane.Other
{
    /// <summary>
    /// Optimized GitHub API manager with caching, retry logic, and better error handling
    /// </summary>
    public class GithubManager : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        
        // Cache for API responses to reduce redundant calls
        private readonly Dictionary<string, (object data, DateTime timestamp)> _cache = new();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);
        
        // Retry configuration
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 1000;

        public GithubManager()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            
            // Use a realistic browser user agent to avoid rate limiting
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }

        private class GitHubContent
        {
            public string? name { get; set; }
            public string? type { get; set; }
            public string? download_url { get; set; }
        }

        private class GitHubRelease
        {
            public string? tag_name { get; set; }
            public string? name { get; set; }
            public string? body { get; set; }
            public DateTime published_at { get; set; }
            public List<GitHubAsset>? assets { get; set; }
        }

        private class GitHubAsset
        {
            public string? name { get; set; }
            public string? browser_download_url { get; set; }
            public long size { get; set; }
            public int download_count { get; set; }
        }

        /// <summary>
        /// Gets the latest release info with caching and retry logic
        /// </summary>
        public async Task<(string tagName, string downloadUrl, long size)> GetLatestReleaseInfo(string owner, string repo)
        {
            string cacheKey = $"release_{owner}_{repo}";
            
            // Check cache first
            if (TryGetCached<GitHubRelease>(cacheKey, out var cachedRelease))
            {
                var cachedAsset = cachedRelease?.assets?.FirstOrDefault();
                if (cachedAsset != null)
                {
                    return (cachedRelease!.tag_name ?? "unknown", 
                            cachedAsset.browser_download_url ?? "", 
                            cachedAsset.size);
                }
            }

            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            
            var release = await ExecuteWithRetryAsync<GitHubRelease>(apiUrl);
            
            if (release == null)
            {
                throw new InvalidOperationException("Failed to fetch release information");
            }

            // Cache the result
            SetCached(cacheKey, release!);


            var asset = release.assets?.FirstOrDefault();
            if (asset == null)
            {
                throw new InvalidOperationException("No assets found in the latest release");
            }

            return (release.tag_name ?? "unknown", asset.browser_download_url ?? "", asset.size);
        }

        /// <summary>
        /// Fetches GitHub directory contents with caching
        /// </summary>
        public async Task<IEnumerable<string>> FetchGithubFilesAsync(string url)
        {
            string cacheKey = $"files_{url.GetHashCode()}";
            
            if (TryGetCached<List<GitHubContent>>(cacheKey, out var cachedContent))
            {
                return cachedContent?.Select(c => c.name).Where(n => n != null).Cast<string>() 
                    ?? Enumerable.Empty<string>();
            }

            var contents = await ExecuteWithRetryAsync<List<GitHubContent>>(url);
            
            if (contents == null)
            {
                throw new InvalidOperationException("Failed to fetch GitHub directory contents");
            }

            SetCached(cacheKey, contents);

            return contents.Select(c => c.name).Where(n => n != null).Cast<string>();
        }

        /// <summary>
        /// Downloads a file from GitHub with progress tracking
        /// </summary>
        public async Task<byte[]> DownloadFileAsync(string url, IProgress<double>? progress = null)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloadedBytes = 0L;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();

            var buffer = new byte[8192];
            int read;
            
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await memoryStream.WriteAsync(buffer, 0, read);
                downloadedBytes += read;
                
                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)downloadedBytes / totalBytes);
                }
            }

            return memoryStream.ToArray();
        }

        /// <summary>
        /// Gets release notes for a specific version
        /// </summary>
        public async Task<string?> GetReleaseNotes(string owner, string repo, string version)
        {
            string cacheKey = $"notes_{owner}_{repo}_{version}";
            
            if (TryGetCached<string>(cacheKey, out var cachedNotes))
            {
                return cachedNotes;
            }

            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{version}";
            var release = await ExecuteWithRetryAsync<GitHubRelease>(apiUrl);
            
            if (release == null) return null;
            
            SetCached(cacheKey, release.body);
            return release.body;
        }

        /// <summary>
        /// Checks if a newer version is available
        /// </summary>
        public async Task<bool> IsUpdateAvailable(string owner, string repo, string currentVersion)
        {
            try
            {
                var (latestVersion, _, _) = await GetLatestReleaseInfo(owner, repo);
                
                // Normalize version strings
                currentVersion = currentVersion.TrimStart('v', 'V');
                latestVersion = latestVersion.TrimStart('v', 'V');
                
                var current = Version.Parse(currentVersion);
                var latest = Version.Parse(latestVersion);
                
                return latest > current;
            }
            catch
            {
                return false;
            }
        }

        #region Private Methods

        /// <summary>
        /// Executes HTTP request with retry logic
        /// </summary>
        private async Task<T?> ExecuteWithRetryAsync<T>(string url) where T : class
        {
            Exception? lastException = null;
            
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    await _semaphore.WaitAsync();
                    try
                    {
                        using var response = await _httpClient.GetAsync(url);
                        
                        // Handle rate limiting
                        if ((int)response.StatusCode == 403)
                        {
                            var resetTime = response.Headers.TryGetValues("X-RateLimit-Reset", out var values) 
                                ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(values.First())) 
                                : DateTimeOffset.UtcNow.AddMinutes(1);
                            
                            var waitTime = resetTime - DateTimeOffset.UtcNow;
                            if (waitTime > TimeSpan.Zero && waitTime < TimeSpan.FromMinutes(5))
                            {
                                await Task.Delay(waitTime);
                                continue;
                            }
                        }
                        
                        response.EnsureSuccessStatusCode();
                        
                        var content = await response.Content.ReadAsStringAsync();
                        return JsonConvert.DeserializeObject<T>(content);
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
                catch (HttpRequestException ex) when (attempt < MaxRetries - 1)
                {
                    lastException = ex;
                    await Task.Delay(RetryDelayMs * (attempt + 1)); // Exponential backoff
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    break;
                }
            }
            
            throw new InvalidOperationException($"Failed after {MaxRetries} attempts", lastException);
        }

        /// <summary>
        /// Tries to get cached data
        /// </summary>
        private bool TryGetCached<T>(string key, out T? data) where T : class
        {
            data = null;
            
            if (_cache.TryGetValue(key, out var cached))
            {
                if (DateTime.UtcNow - cached.timestamp < _cacheDuration)
                {
                    data = cached.data as T;
                    return true;
                }
                _cache.Remove(key);
            }
            
            return false;
        }

        /// <summary>
        /// Sets cached data
        /// </summary>
        private void SetCached(string key, object? data)
        {
            _cache[key] = (data ?? new object(), DateTime.UtcNow);
        }


        /// <summary>
        /// Clears all cached data
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }

        #endregion

        public void Dispose()
        {
            _httpClient.Dispose();
            _semaphore.Dispose();
            _cache.Clear();
        }
    }
}
