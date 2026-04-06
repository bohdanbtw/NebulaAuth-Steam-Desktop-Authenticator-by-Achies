# Steps to Create and Push the PR

## Step 1: Verify the branch is clean
```powershell
cd D:\Programming\NebulaAuth\NebulaAuth-Steam-Desktop-Authenticator-by-Achies
git status
```

## Step 2: Create the commit
```powershell
git commit -m "feat: Add embedded Steam inventory with WebView2 and automatic QR authentication

- Implement automatic QR-code-based authentication using mafile authenticator
- Add embedded WebView2 browser for Steam inventory viewing
- Integrate C#-level protobuf interception for QR session capture
- Auto-download and inject Steam Inventory Helper extension
- Implement per-account WebView2 profiles for isolated web storage
- Add single-instance enforcement with window activation
- Add dark overlay with loading spinner during login
- Add UI converters and inventory window components
- Add comprehensive implementation and technical documentation

Features:
- QR login without external QR decoders (no CSP issues)
- Direct Steam API approval via UpdateAuthSessionWithMobileConfirmation
- Automatic browser redirect after successful authentication
- Steam Inventory Helper auto-downloaded from Chrome Web Store
- Per-account browser profiles (isolated cookies/sessions)
- Single-instance app enforcement (reactivates existing window)
- Maximized window with refresh button
- Non-blocking extension loading (graceful fallback if download fails)

Technical:
- C#-level HTTP response interception via WebResourceResponseReceived
- Protobuf deserialization for BeginAuthSessionViaQR response
- CRX3/CRX2 format parsing and ZIP extraction
- Mutex-based single instance with Windows API window activation
- Dark Steam-themed overlay with MaterialDesign spinner

Dependencies:
- Microsoft.Web.WebView2.Wpf v1.0.2592.51
- protobuf-net v3.2.52
- No new external dependencies added

Storage:
- Browser profiles: %LocalAppData%/NebulaAuth/WebView2Profiles/{accountName}/
- Extensions: %LocalAppData%/NebulaAuth/Extensions/SteamInventoryHelper/"
```

## Step 3: Push to your fork
```powershell
git push origin feat/embedded-steam-inventory-webview2
```

## Step 4: Create PR on GitHub
Go to: https://github.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/compare/master...bohdanbtw:NebulaAuth-Steam-Desktop-Authenticator-by-Achies:feat/embedded-steam-inventory-webview2

**Or manually:**
1. Go to https://github.com/bohdanbtw/NebulaAuth-Steam-Desktop-Authenticator-by-Achies
2. Click "Contribute" → "Open pull request"
3. Set base repo: achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies (master branch)
4. Set head repo: bohdanbtw/NebulaAuth-Steam-Desktop-Authenticator-by-Achies (feat/embedded-steam-inventory-webview2 branch)
5. Use the PR description from PR_DESCRIPTION.md

## Optional: Delete local branch after PR is merged
```powershell
git branch -d feat/embedded-steam-inventory-webview2
```

## Troubleshooting
If you get "nothing to commit":
```powershell
git add -A
git status
git commit -m "..."
```

If you need to amend the last commit:
```powershell
git add -A
git commit --amend --no-edit
git push origin feat/embedded-steam-inventory-webview2 --force
```

If push is rejected (branch already exists):
```powershell
git push origin feat/embedded-steam-inventory-webview2 --force
```
