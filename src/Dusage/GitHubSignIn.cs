using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace Dusage;

/// <summary>
/// dUsage/dt's own GitHub sign-in, for Copilot users without the GitHub CLI (say, only VS Code): GitHub's device flow,
/// where you enter a short code on github.com. Its token is the one thing dUsage/dt keeps itself, in Windows Credential
/// Manager rather than in %LOCALAPPDATA%\dusage, and it's only ever sent to GitHub.
/// </summary>
static class GitHubSignIn
{
    /// <summary>dUsage/dt's OAuth app on GitHub. Public by design: the device flow has no secret.</summary>
    const string ClientId = "";
    const string Target = "dusage:github.com";

    static CancellationTokenSource? _cancel;

    /// <summary>Raised on the UI thread whenever any of the below changes.</summary>
    public static event Action? Changed;

    public static bool Available => ClientId.Length > 0;

    public static string? Token => Native.ReadSecret(Target);

    /// <summary>The code to enter on github.com, while a sign-in waits for it.</summary>
    public static string? UserCode { get; private set; }

    public static bool Busy => _cancel is not null;

    /// <summary>Why the last sign-in didn't work out.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Asks GitHub for a code, copies it and opens the page to enter it on, then waits for the go-ahead.</summary>
    public static async Task StartAsync()
    {
        if (_cancel is not null) return;
        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        Problem = null;
        Changed?.Invoke();
        try
        {
            string deviceCode;
            TimeSpan interval, expiresIn;
            using (var code = await PostAsync("https://github.com/login/device/code", new() { ["client_id"] = ClientId, ["scope"] = "read:user" }, ct))
            {
                var root = code.RootElement;
                deviceCode = root.Str("device_code") ?? throw new UsageException("GitHub didn't hand out a code.");
                UserCode = root.Str("user_code");
                interval = TimeSpan.FromSeconds(root.Num("interval") ?? 5);
                expiresIn = TimeSpan.FromSeconds(root.Num("expires_in") ?? 900);
                try
                {
                    Clipboard.SetText(UserCode ?? "");
                }
                catch (COMException)
                {
                    // Another app holds the clipboard; the code is on screen anyway.
                }
                AppInfo.Open(root.Str("verification_uri") ?? "https://github.com/login/device");
            }
            Changed?.Invoke();

            var deadline = DateTimeOffset.Now + expiresIn;
            while (DateTimeOffset.Now < deadline)
            {
                await Task.Delay(interval, ct);
                using var answer = await PostAsync("https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }, ct);
                var root = answer.RootElement;
                if (root.Str("access_token") is { Length: > 0 } token)
                {
                    Native.WriteSecret(Target, "github.com", token);
                    return;
                }
                switch (root.Str("error"))
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = TimeSpan.FromSeconds(root.Num("interval") ?? interval.TotalSeconds + 5);
                        continue;
                    case "access_denied":
                        Problem = "The sign-in was turned down on GitHub.";
                        return;
                    case "expired_token":
                        Problem = "The code expired before it was entered. Sign in again for a new one.";
                        return;
                    default:
                        Problem = root.Str("error_description") ?? "GitHub didn't finish the sign-in.";
                        return;
                }
            }
            Problem = "The code expired before it was entered. Sign in again for a new one.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled from Settings.
        }
        catch (UsageException e)
        {
            Problem = e.Message;
        }
        catch (Exception e)
        {
            Log.Error(e);
            Problem = "Couldn't sign in: " + e.Message;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            UserCode = null;
            Changed?.Invoke();
        }
    }

    public static void Cancel() => _cancel?.Cancel();

    public static void SignOut()
    {
        Native.DeleteSecret(Target);
        Problem = null;
        Changed?.Invoke();
    }

    /// <summary>GitHub turned the token down (revoked, say): forget it, so Settings offers to sign in again.</summary>
    public static void Rejected()
    {
        Native.DeleteSecret(Target);
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Problem = "GitHub no longer accepts dUsage/dt's sign-in. Sign in again to show Copilot.";
            Changed?.Invoke();
        });
    }

    static async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return await Net.GetJsonAsync(request, ct);
    }
}
