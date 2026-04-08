using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using ProtoBuf;
using SteamLib.Api.Services;
using SteamLib.Authentication;
using SteamLib.Exceptions.Authorization;
using SteamLib.ProtoCore.Services;

namespace NebulaAuth.Helpers;

/// <summary>
/// Automatically approves Steam QR-code login inside the embedded WebView2.
///
///// Flow:
/////   1. The login page JS calls Steam's BeginAuthSessionViaQR API.
/////   2. We intercept the HTTP response in C# via WebResourceResponseReceived
/////      and deserialize the protobuf to extract the clientId.
/////   3. With the clientId + mafile's SharedSecret + SteamId we call
/////      UpdateAuthSessionWithMobileConfirmation — exactly what the Steam Mobile App does.
/////   4. The browser detects the server-side approval and redirects to the inventory.
/// </summary>
public class SteamQRCodeAuthenticator
{
    private CoreWebView2? _coreWebView;
    private Mafile? _mafile;
    private CancellationTokenSource? _cts;
    private bool _approvalInProgress;
    private bool _approved;
    private bool _redirected;
    private HttpClient? _authClient;
    private string? _accessToken;
    private Task? _authPreparationTask;

    public event EventHandler<AuthStatusEventArgs>? StatusChanged;

    public Task InitializeAsync(CoreWebView2 coreWebView, Mafile mafile)
    {
        _coreWebView = coreWebView;
        _mafile = mafile;
        _cts = new CancellationTokenSource();

        _coreWebView.WebResourceResponseReceived += OnWebResourceResponseReceived;
        _coreWebView.NavigationCompleted += OnNavigationCompleted;

        // Safe prewarm only (no refresh here) so UI starts instantly without risking state conflicts.
        _authPreparationTask = PrewarmAuthContextAsync();

        var url = "https://steamcommunity.com/login/home/?goto=%2Fmy%2Finventory%2F";
        _coreWebView.Navigate(url);

        StatusChanged?.Invoke(this, new AuthStatusEventArgs
        {
            Status = "Waiting for Steam login page...",
            IsLoading = true
        });

        return Task.CompletedTask;
    }

    private async Task PrewarmAuthContextAsync()
    {
        try
        {
            await PrepareAuthContextAsync(refreshIfNeeded: false);
        }
        catch
        {
            // Prewarm failure is non-fatal; real preparation happens on approval.
            _authPreparationTask = null;
        }
    }

    private async Task PrepareAuthContextAsync(bool refreshIfNeeded)
    {
        if (_mafile?.SessionData == null)
        {
            throw new SessionInvalidException();
        }

        var token = _mafile.SessionData.GetMobileToken();
        if ((token == null || token.Value.IsExpired) && refreshIfNeeded)
        {
            StatusChanged?.Invoke(this, new AuthStatusEventArgs
            {
                Status = "Refreshing mobile session...",
                IsLoading = true
            });

            await MaClient.RefreshSession(_mafile);
            token = _mafile.SessionData?.GetMobileToken();
        }

        if (token == null || token.Value.IsExpired)
        {
            throw new SessionPermanentlyExpiredException();
        }

        _authClient = MaClient.GetHttpClientHandlerPair(_mafile).Client;
        _accessToken = token.Value.Token;
    }

    private async Task EnsureAuthContextReadyAsync()
    {
        if (_authClient != null && !string.IsNullOrWhiteSpace(_accessToken))
        {
            return;
        }

        if (_authPreparationTask != null)
        {
            try
            {
                await _authPreparationTask;
                if (_authClient != null && !string.IsNullOrWhiteSpace(_accessToken))
                {
                    return;
                }
            }
            catch
            {
                // Ignore and rebuild below.
            }
            finally
            {
                _authPreparationTask = null;
            }
        }

        await PrepareAuthContextAsync(refreshIfNeeded: true);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _coreWebView == null) return;

        var uri = _coreWebView.Source;

        if (uri.Contains("/inventory", StringComparison.OrdinalIgnoreCase) &&
            !uri.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            StatusChanged?.Invoke(this, new AuthStatusEventArgs
            {
                Status = "Login successful! Viewing inventory.",
                IsLoading = false
            });
        }
        else if (uri.Contains("login", StringComparison.OrdinalIgnoreCase) && !_approved)
        {
            StatusChanged?.Invoke(this, new AuthStatusEventArgs
            {
                Status = "Steam login page loaded. Waiting for QR session...",
                IsLoading = true
            });
        }
    }

    private async void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var requestUri = e.Request.Uri;

            if (requestUri.Contains("BeginAuthSessionViaQR", StringComparison.OrdinalIgnoreCase))
            {
                await HandleQRSessionResponseAsync(e);
            }
            else if (_approved && !_redirected && requestUri.Contains("finalizelogin", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Response.StatusCode == 200)
                {
                    StatusChanged?.Invoke(this, new AuthStatusEventArgs
                    {
                        Status = "Session established! Loading inventory...",
                        IsLoading = true
                    });
                }
            }
            else if (_approved && !_redirected && requestUri.Contains("/settoken", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Response.StatusCode == 200)
                {
                    _redirected = true;
                    await Task.Delay(120);
                    _coreWebView?.Navigate("https://steamcommunity.com/my/inventory/");
                }
            }
        }
        catch (Exception)
        {
            // Response interception error handled silently
        }
    }

    private async Task HandleQRSessionResponseAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_approvalInProgress || _approved) return;

        if (e.Response.StatusCode != 200)
        {
            return;
        }

        try
        {
            using var stream = await e.Response.GetContentAsync();
            if (stream == null)
            {
                return;
            }

            var qrResponse = Serializer.Deserialize<BeginAuthSessionViaQR_Response>(stream);
            if (qrResponse == null || qrResponse.ClientId == 0)
            {
                return;
            }

            _approvalInProgress = true;
            StatusChanged?.Invoke(this, new AuthStatusEventArgs
            {
                Status = "QR session captured! Approving with authenticator...",
                IsLoading = true
            });

            _ = ApproveQRSessionAsync(qrResponse.ClientId);
        }
        catch (Exception)
        {
            // Error parsing QR protobuf handled silently
        }
    }

    private async Task ApproveQRSessionAsync(ulong clientId)
    {
        if (_mafile?.SharedSecret == null || _mafile.SessionData == null)
        {
            ReportError("Authenticator data missing. Ensure the account is set up in the main app.");
            _approvalInProgress = false;
            return;
        }

        try
        {
            await EnsureAuthContextReadyAsync();
            await SendApprovalAsync(clientId);

            _approved = true;
            StatusChanged?.Invoke(this, new AuthStatusEventArgs
            {
                Status = "Login approved! Finalizing browser session...",
                IsLoading = true
            });
        }
        catch (Exception ex) when (IsInvalidState(ex))
        {
            try
            {
                _authClient = null;
                _accessToken = null;
                _authPreparationTask = null;

                await EnsureAuthContextReadyAsync();
                await SendApprovalAsync(clientId);

                _approved = true;
                StatusChanged?.Invoke(this, new AuthStatusEventArgs
                {
                    Status = "Login approved! Finalizing browser session...",
                    IsLoading = true
                });
            }
            catch (SessionPermanentlyExpiredException)
            {
                ReportError("Session expired. Please re-authenticate in the main app.");
            }
            catch (SessionInvalidException)
            {
                ReportError("Session invalid. Please log in again in the main app.");
            }
            catch (Exception retryEx)
            {
                ReportError($"Error: {retryEx.Message}");
            }
        }
        catch (SessionPermanentlyExpiredException)
        {
            ReportError("Session expired. Please re-authenticate in the main app.");
        }
        catch (SessionInvalidException)
        {
            ReportError("Session invalid. Please log in again in the main app.");
        }
        catch (Exception ex)
        {
            ReportError($"Error: {ex.Message}");
        }
        finally
        {
            if (!_approved)
                _approvalInProgress = false;
        }
    }

    private async Task SendApprovalAsync(ulong clientId)
    {
        if (_authClient == null || string.IsNullOrWhiteSpace(_accessToken) || _mafile?.SessionData == null)
        {
            throw new SessionInvalidException("Session context not prepared");
        }

        var confirmReq = AuthRequestHelper.CreateMobileConfirmationRequest(
            1, clientId, _mafile.SessionData.SteamId, _mafile.SharedSecret);

        StatusChanged?.Invoke(this, new AuthStatusEventArgs
        {
            Status = "Approving login request...",
            IsLoading = true
        });

        await AuthenticationServiceApi.UpdateAuthSessionWithMobileConfirmation(
            _authClient, _accessToken, confirmReq);
    }

    private static bool IsInvalidState(Exception ex)
    {
        return ex.Message.Contains("0x8007139F", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("not in the correct state", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportError(string message)
    {
        StatusChanged?.Invoke(this, new AuthStatusEventArgs
        {
            Status = message,
            IsLoading = false,
            IsError = true
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_coreWebView != null)
        {
            _coreWebView.WebResourceResponseReceived -= OnWebResourceResponseReceived;
            _coreWebView.NavigationCompleted -= OnNavigationCompleted;
        }

        _authPreparationTask = null;
    }
}

public class AuthStatusEventArgs : EventArgs
{
    public string Status { get; set; } = string.Empty;
    public bool IsLoading { get; set; }
    public bool IsError { get; set; }
}
