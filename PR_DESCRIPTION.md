# Embedded Steam Inventory with WebView2 & Automatic QR Login

## Overview
This PR adds a fully embedded Steam inventory browser window to NebulaAuth with **automatic QR-code-based authentication** using the mafile authenticator. Users can now view their Steam inventory directly within the application without logging in separately.

## Key Features

### 1. **Embedded WebView2 Browser** (`InventoryWindow.xaml`)
- Opens Steam Community inventory in a maximized window
- Operates as a child window of the main application
- Clean, distraction-free interface with header toolbar
- Per-account browser profiles (isolated web storage per account)

### 2. **Automatic QR Authentication** (`SteamQRCodeAuthenticator.cs`)
- **C#-level protobuf interception**: Intercepts Steam's `BeginAuthSessionViaQR` HTTP response via WebView2's `WebResourceResponseReceived` event
- **No external QR decoders**: Eliminates CSP issues by working at the HTTP response level instead of JavaScript
- **Protobuf deserialization**: Extracts `clientId` from binary protobuf response (not JSON)
- **Direct Steam API approval**: Calls `UpdateAuthSessionWithMobileConfirmation` using mafile's `SharedSecret` and `SteamId` — exactly what the Steam Mobile App does
- **Server-side validation**: Steam's servers validate the approval and set session cookies
- **Automatic redirect**: Browser's own polling loop handles session token exchange and redirect to inventory
- **No forced navigation**: Respects the browser's natural cookie-setting flow to prevent infinite loops

### 3. **Steam Inventory Helper Extension** (`ExtensionManager.cs`)
- Automatically downloads Steam Inventory Helper from Chrome Web Store
- Extracts CRX3/CRX2 format files to local directory (`%LocalAppData%/NebulaAuth/Extensions/SteamInventoryHelper/`)
- Loads extension into WebView2 browser profile automatically on window open
- Caches downloaded extension to avoid re-downloading
- Non-blocking: Browser works without the extension if download fails

### 4. **Enhanced UX**
- **Dark overlay with spinner**: Displays during login, hides after QR approval
- **Status messages**: Real-time feedback on authentication progress
- **Refresh button**: Manually reload inventory page
- **Maximized window**: Opens in fullscreen for better visibility
- **Single instance enforcement**: Running the executable again activates the existing window instead of launching a new instance
- **Proper z-order**: Browser window stays on top of main application

## Technical Details

### Architecture
```
User opens inventory
    ↓
InventoryWindow initialized with per-account WebView2 profile
    ↓
QR login page loads (https://steamcommunity.com/login/home/?goto=%2Fmy%2Finventory%2F)
    ↓
Browser's JS calls BeginAuthSessionViaQR → C# intercepts response
    ↓
Extract clientId from protobuf → GetAuthSessionInfo + CreateMobileConfirmationRequest
    ↓
UpdateAuthSessionWithMobileConfirmation (server-side approval)
    ↓
Browser's JS polling receives tokens → finalizelogin → settoken → inventory redirect
    ↓
User views inventory
```

### New/Modified Files

**New Files:**
- `src/NebulaAuth/Helpers/SteamQRCodeAuthenticator.cs` — QR authentication engine
- `src/NebulaAuth/Helpers/ExtensionManager.cs` — Extension downloader & loader
- `src/NebulaAuth/View/InventoryWindow.xaml` — Browser UI with overlay
- `src/NebulaAuth/View/InventoryWindow.xaml.cs` — Window code-behind
- `src/NebulaAuth/ViewModel/InventoryVM.cs` — ViewModel for inventory window
- `src/NebulaAuth/ViewModel/MainVM_Inventory.cs` — OpenInventory command
- `src/NebulaAuth/Converters/{NotNullToVisibilityConverter, NullToVisibilityConverter, ObjectEqualityConverter}.cs` — UI converters
- `STEAM_LOGIN_IMPLEMENTATION.md` — Implementation notes (technical reference)
- `WEBVIEW2_AUTHENTICATION_NOTES.md` — WebView2 auth flow documentation

**Modified Files:**
- `src/NebulaAuth/App.xaml.cs` — Single-instance enforcement using Mutex + Windows API
- `src/NebulaAuth/MainWindow.xaml.cs` — Added OpenInventory command binding
- `src/NebulaAuth/MainWindow.xaml` — Added inventory button to account list
- `src/NebulaAuth/Model/MaClient.cs` — Exposed `GetHttpClientHandlerPair()` for QR authenticator
- `src/SteamLibForked/ProtoCore/Services/AuthenticationService.cs` — Added `BeginAuthSessionViaQR_Response` protobuf type
- `src/NebulaAuth/Converters/Converters.xaml` — Registered new converters

### Protobuf Integration
Added `BeginAuthSessionViaQR_Response` protobuf message type:
```csharp
[ProtoContract]
public class BeginAuthSessionViaQR_Response
{
    [ProtoMember(1)] public ulong ClientId { get; set; }
    [ProtoMember(2)] public string ChallengeUrl { get; set; }
    [ProtoMember(3)] public ulong RequestId { get; set; }
    [ProtoMember(4)] public int Interval { get; set; }
    [ProtoMember(5)] public string AllowedConfirmations { get; set; }
    [ProtoMember(6)] public int Version { get; set; }
}
```

### Apparel & Dependencies
- **Microsoft.Web.WebView2.Wpf** (v1.0.2592.51 transitive)
- **protobuf-net** (v3.2.52)
- **CommunityToolkit.Mvvm** (existing)
- **MaterialDesignInXaml** (existing)

### Storage Locations
- **Browser profiles**: `%LocalAppData%/NebulaAuth/WebView2Profiles/{accountName}/`
- **Extensions**: `%LocalAppData%/NebulaAuth/Extensions/SteamInventoryHelper/`

## Testing Checklist
- [ ] Open inventory with valid mafile account
- [ ] QR code appears on login page
- [ ] Approve login on mobile Steam app or with authenticator
- [ ] Browser automatically redirects to inventory
- [ ] Steam Inventory Helper extension loads
- [ ] Minimize and reopen app — window reactivates (single instance)
- [ ] Browser window stays on top of main window
- [ ] Per-account profiles work (login one account, then different account has fresh cookies)
- [ ] Close and reopen inventory — profile data persists

## Breaking Changes
None. This is a purely additive feature.

## Notes
- The extension manager handles `CRX2` and `CRX3` formats and parses the ZIP payload correctly
- Per-account WebView2 profiles are isolated by account name from the mafile — cookies/sessions never cross-contaminate
- Single-instance enforcement uses a named `Mutex` (`NebulaAuth_SingleInstance`) and Windows API calls (`SetForegroundWindow`, `ShowWindow`)
- The QR authenticator is completely self-contained; it doesn't require any user interaction beyond scanning the initial QR code on the Steam login page

## Related Issues
Closes #[issue-number] (if applicable)

---

**Author Notes**:
This implementation prioritizes **zero additional user friction** — the browser handles everything after QR approval. No modal dialogs, no "waiting for token" screens, no forced navigation that breaks cookies. The browser's own polling loop is trusted to do the right thing, which eliminates all the infinite loop issues from earlier approaches.
