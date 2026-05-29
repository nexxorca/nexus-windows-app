<!-- plan-modernize-ui.md | c:\xampp\htdocs\nexus-windows-app\project_docs\plans\plan-modernize-ui.md -->

> Author: Erik | Agent: tech-lead | Created: 2026-05-28 (rewrite of original WPF-UI/Fluent plan) | Status: Proposed

# Modernize UI — Implementation Plan

## Context

All four windows (`LoginWindow`, `SettingsWindow`, `ActivityLogWindow`, `MainWindow`) use raw default WPF chrome — system fonts, grey title bar, default button shading. With `MainWindow` shipping in 1.1.0, the visual gap from a modern desktop app is now obvious to every user.

The PM chose `jksfinancial_desktop`'s visual language as the target: flat custom design, sidebar + topbar shell, white card login on dark backdrop, restrained palette. JKS Desktop achieves that look with hand-rolled XAML and zero theme libraries — but with no design system (inline templates, hardcoded colors, no hover states).

Rather than clone JKS's architectural debt, we extract the visual language into a new shared library — **`Nexxor.Wpf.Ui`** (see `nexus/project_docs/plans/plan-nexxor-wpf-ui-library.md`). This Nexus modernization plan **consumes that library as its first real adopter**, validating the library's API while delivering Nexus's modern UI.

This plan also incorporates the `MainWindow` layout fixes flagged after 1.1.0 shipped (empty middle gap, vertical right-aligned button stack, version label misplaced). Those become trivial once the shell layout comes from the library.

**Dependency:** `plan-nexxor-wpf-ui-library.md` must reach Step 6 (build succeeds + demo consumer proven) before this plan starts Step 2.

---

## Architecture / Approach

```
Before (1.1.0):                        After (1.2.0):

Window: LoginWindow                    OverlayLoginWindow (library base)
Window: MainWindow                     AppShellWindow (library base)
Window: SettingsWindow                 ─┐
Window: ActivityLogWindow              ─┘─> become Pages inside AppShellWindow.ContentFrame

Tray menu opens 4 separate windows.    Tray menu opens MainWindow + navigates the shell.
Sync state shown in MainWindow only.   Sync state shown on the Home page (default selected).
```

**Architectural shift:** Settings and ActivityLog stop being separate `<Window>`s and become `<Page>`s rendered inside `MainWindow`'s content `Frame`, navigated via the left sidebar. The tray menu items "Settings" / "Activity Log" now open MainWindow and select the appropriate sidebar item, instead of opening detached windows.

**Login flow unchanged.** `OverlayLoginWindow` replaces `LoginWindow` — same `LoginSucceeded` event, same `App.xaml.cs` wiring.

**Tray + SyncEngine unchanged.** All sync orchestration in `App.xaml.cs`, `TrayIconManager`, `SyncEngine` is untouched. Only the view layer changes.

---

## Decisions / Rejected Options

| Option | Reason rejected | Ruling date |
|--------|-----------------|-------------|
| Add WPF-UI (Lepoco Fluent) library | Wrong aesthetic — PM chose JKS-style flat custom, not Fluent. See library plan's decision table. | 2026-05-28 |
| Aesthetic-only restyle of existing 4 separate windows | Half-measure — sidebar + topbar shell is the defining JKS pattern. Settings/ActivityLog as detached windows undermines the look. | 2026-05-28 |
| Standard window chrome for login (skip the borderless overlay) | Loses half the JKS visual signature. Borderless overlay is the defining "app-like, not OS-like" choice. | 2026-05-28 |
| Build Nexus UI inline (no shared library) | Reproduces JKS's maintenance debt. Library extraction happens NOW so Nexus is the first consumer. | 2026-05-28 |
| Inline hex colors in this project | All colors come from library's named brushes. Consumers override `AccentBrush` etc. in their own App.xaml — never re-introduce hex literals. | 2026-05-28 |
| Defer version bump to `/push-dev` | Plan bumps version per project precedent (`plan-main-window.md` Step 5). | 2026-05-28 |
| Dark theme at launch | PM hasn't asked — defer to library's `improvements.md`. One-line theme swap once added. | 2026-05-28 |

---

## Plan Overview

1. **Reference the library** — add `ProjectReference` to `Nexxor.Wpf.Ui`, merge theme into `App.xaml`.
2. **Convert LoginWindow** → `OverlayLoginWindow` base class (borderless overlay + white card).
3. **Build shell + convert windows to Pages** — `MainWindow` becomes `AppShellWindow`; `SettingsWindow` and `ActivityLogWindow` become `Page`s loaded into the shell's `Frame`.
4. **Wire tray menu to shell navigation** — tray items open MainWindow and select the right sidebar item instead of opening detached windows.
5. **Polish pass + screenshot review** — verify each surface visually, primary accent on default-action buttons.
6. **Docs & version** — update `structure.md`, `CHANGELOG.md`, bump csproj 1.1.0 → 1.2.0.
7. **Close-out** — verification checklist.

---

## Steps

### Step 1: Reference the library

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 1.1 | Add `<ProjectReference Include="..\..\..\nexxor-wpf-ui\src\Nexxor.Wpf.Ui\Nexxor.Wpf.Ui.csproj"/>` to `Nexus.App.csproj` (path assumes both repos sit under `c:\xampp\htdocs\`) | dev | MODIFY | `src/Nexus.App/Nexus.App.csproj` | TODO |
| 1.2 | Merge library theme into `App.xaml` — `<ResourceDictionary Source="pack://application:,,,/Nexxor.Wpf.Ui;component/Themes/Default.xaml"/>` inside `Application.Resources.MergedDictionaries`. Preserve `OnExplicitShutdown` mode. | dev | MODIFY | `src/Nexus.App/App.xaml` | TODO |
| 1.3 | **Build gate** — `dotnet build src/Nexus.App/Nexus.App.csproj -c Release` must succeed. If the library reference fails (e.g., path mismatch), halt and re-verify the library plan's Step 6 (demo consumer) passed first. | dev | RUN | — | TODO |

### Step 2: Convert LoginWindow

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 2.1 | `LoginWindow.xaml` — change root to `<ui:OverlayLoginWindow>` (`xmlns:ui="clr-namespace:Nexxor.Wpf.Ui.Windows;assembly=Nexxor.Wpf.Ui"`). Move the existing email / password / Nexus URL fields into the library's `CardContent` slot. Login button uses `Style="{StaticResource PrimaryButton}"`. Remove all hardcoded colors and explicit `Height` attributes. | dev | MODIFY | `src/Nexus.App/Views/LoginWindow.xaml` | TODO |
| 2.2 | `LoginWindow.xaml.cs` — change base class to `Nexxor.Wpf.Ui.Windows.OverlayLoginWindow`. Preserve `LoginSucceeded` event, all existing API client wiring, and any `OnClosing`/`OnClosed` behavior. | dev | MODIFY | `src/Nexus.App/Views/LoginWindow.xaml.cs` | TODO |
| 2.3 | **Verify** — build + launch the app, log out → LoginWindow renders as borderless overlay; drag-to-move works; Escape closes; login still succeeds end-to-end. Halt the plan if any of these regress. | dev | RUN | — | TODO |

### Step 3: Build shell + convert windows to Pages

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 3.1 | Create `Views/Pages/` directory. Create `HomePage.xaml(.cs)` — current MainWindow content (state panel: user, last sync, status, Sync Now button) moves here. Subscribe to `SyncEngine.SyncStarted/SyncCompleted` events on UI dispatcher per existing ordering invariant in structure.md. | dev | CREATE | `src/Nexus.App/Views/Pages/HomePage.xaml`, `.cs` | TODO |
| 3.2 | `SettingsWindow.xaml(.cs)` → `Views/Pages/SettingsPage.xaml(.cs)`. Change root from `<Window>` to `<Page>`. Move existing form contents (sync interval, launch-on-startup toggle, version display) verbatim. Save button uses `Style="{StaticResource PrimaryButton}"`. Delete the original `SettingsWindow` files. | dev | CREATE+DELETE | `src/Nexus.App/Views/Pages/SettingsPage.xaml`, `.cs` (create); `src/Nexus.App/Views/SettingsWindow.xaml`, `.cs` (delete) | TODO |
| 3.3 | `ActivityLogWindow.xaml(.cs)` → `Views/Pages/ActivityLogPage.xaml(.cs)`. Same Window→Page conversion. ListView keeps current columns. Delete original `ActivityLogWindow` files. | dev | CREATE+DELETE | `src/Nexus.App/Views/Pages/ActivityLogPage.xaml`, `.cs` (create); `src/Nexus.App/Views/ActivityLogWindow.xaml`, `.cs` (delete) | TODO |
| 3.4 | `MainWindow.xaml` — change root to `<ui:AppShellWindow>`. Declare 3 `NavItem`s (Home, Activity, Settings — Segoe Fluent glyphs `&#xE80F;`, `&#xE7C3;`, `&#xE713;`). `AppTitle="Nexus"`. `TopRightNavBar` populated with current user name from `AppConfig` + logout command. | dev | MODIFY | `src/Nexus.App/MainWindow.xaml` | TODO |
| 3.5 | `MainWindow.xaml.cs` — change base class to `Nexxor.Wpf.Ui.Windows.AppShellWindow`. Keep singleton + `OnClosing` hide-instead-of-close behavior. Remove the old state-panel/button-stack code (now lives in `HomePage`). | dev | MODIFY | `src/Nexus.App/MainWindow.xaml.cs` | TODO |
| 3.6 | **Build + launch verify** — open MainWindow → sidebar shows 3 items; Home is selected by default; clicking Activity/Settings navigates the `Frame`; state panel on Home displays current sync state correctly. Halt if any regress. | dev | RUN | — | TODO |

### Step 4: Wire tray menu to shell navigation

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 4.1 | `TrayIconManager.cs` — "Activity Log" and "Settings" menu items no longer open separate windows. Instead they call `App.ShowMainWindow(NavTarget.Activity)` / `NavTarget.Settings`. "Open Nexus" stays — defaults to `NavTarget.Home`. | dev | MODIFY | `src/Nexus.App/TrayIconManager.cs` | TODO |
| 4.2 | `App.xaml.cs` — add `ShowMainWindow(NavTarget target)` method that ensures `MainWindow` is visible and selects the matching `NavItem`. Remove the `ShowSettingsWindow` / `ShowActivityLogWindow` action methods (now redundant). | dev | MODIFY | `src/Nexus.App/App.xaml.cs` | TODO |
| 4.3 | Add `NavTarget` enum: `Home`, `Activity`, `Settings`. | dev | CREATE | `src/Nexus.App/NavTarget.cs` | TODO |
| 4.4 | **Verify** — right-click tray → each menu item routes to MainWindow with correct page selected; rapid clicks don't open duplicates; close MainWindow → tray menu still functions. | dev | RUN | — | TODO |

### Step 5: Polish pass + screenshot review

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 5.1 | Manual visual walk — launch the app, screenshot each surface: Login overlay, MainWindow with Home selected (sync running + idle states), Activity page, Settings page. Compare against the verification list. Fix any alignment regressions. | dev | RUN | — | TODO |
| 5.2 | Tech-lead reviews screenshots before declaring 5.1 Done. | tech-lead | RUN | — | TODO |

### Step 6: Docs & version

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 6.1 | Update `project_docs/structure.md`: (a) add `Nexxor.Wpf.Ui` to Key Dependencies, (b) update "Last updated" date, (c) replace Windows/Views section — `SettingsWindow`/`ActivityLogWindow` removed, `Views/Pages/HomePage,SettingsPage,ActivityLogPage` added, MainWindow now inherits `AppShellWindow`, LoginWindow now inherits `OverlayLoginWindow`, (d) fix the existing path-block bug (windows shown at root but actually in `Views/`). | doc-writer | MODIFY | `project_docs/structure.md` | TODO |
| 6.2 | `CHANGELOG.md` entry for 1.2.0 — JKS-style visual modernization, MainWindow shell layout, Settings/ActivityLog converted from windows to in-shell pages, library dependency added. | dev | MODIFY | `CHANGELOG.md` | TODO |
| 6.3 | Bump `<Version>` in `Nexus.App.csproj` from 1.1.0 to 1.2.0. | dev | MODIFY | `src/Nexus.App/Nexus.App.csproj` | TODO |

### Step 7 (final — always last): Close-out

| # | Description | Agent | Action | Files | Status |
|---|-------------|-------|--------|-------|--------|
| 7.1 | Propose close-out checklist per Rule 21 (chat only — not written to plan) | tech-lead | RUN | — | TODO |

---

## Detailed Sub-steps

### 1.2 App.xaml merge

```xml
<Application x:Class="Nexus.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/Nexxor.Wpf.Ui;component/Themes/Default.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

No per-app brush overrides for 1.2.0 — Nexus uses the library defaults. Brand-color override (orange-ish accent, if PM wants distinct branding from JKS later) goes here in a future version.

### 3.4 MainWindow.xaml shape

```xml
<ui:AppShellWindow x:Class="Nexus.App.MainWindow"
                   xmlns:ui="clr-namespace:Nexxor.Wpf.Ui.Windows;assembly=Nexxor.Wpf.Ui"
                   xmlns:m="clr-namespace:Nexxor.Wpf.Ui.Models;assembly=Nexxor.Wpf.Ui"
                   AppTitle="Nexus"
                   Title="Nexus"
                   Height="700" Width="1100"
                   WindowStartupLocation="CenterScreen">
    <ui:AppShellWindow.NavItems>
        <m:NavItem Icon="&#xE80F;" Label="Home"     PageUri="/Views/Pages/HomePage.xaml"/>
        <m:NavItem Icon="&#xE7C3;" Label="Activity" PageUri="/Views/Pages/ActivityLogPage.xaml"/>
        <m:NavItem Icon="&#xE713;" Label="Settings" PageUri="/Views/Pages/SettingsPage.xaml"/>
    </ui:AppShellWindow.NavItems>
</ui:AppShellWindow>
```

The `TopRightNavBar` content (user name + logout) is populated in `MainWindow.xaml.cs` `RefreshState()` — reads `AppConfig.UserName`, wires logout command to the existing `App.Logout()` flow.

### 4.2 ShowMainWindow signature

```csharp
public void ShowMainWindow(NavTarget target = NavTarget.Home) {
    if (_mainWindow is null || !_mainWindow.IsLoaded) {
        _mainWindow = new MainWindow();
    }
    _mainWindow.NavigateTo(target);
    _mainWindow.Show();
    _mainWindow.Activate();
}
```

`MainWindow.NavigateTo(NavTarget target)` maps the enum to the corresponding `NavItem` in the shell's `NavItems` collection and sets `SelectedNavItem`.

---

## File Summary

### Created Files

| File | Purpose |
|------|---------|
| `src/Nexus.App/Views/Pages/HomePage.xaml(.cs)` | Sync state panel + Sync Now button (was the MainWindow body) |
| `src/Nexus.App/Views/Pages/SettingsPage.xaml(.cs)` | Settings form (converted from SettingsWindow) |
| `src/Nexus.App/Views/Pages/ActivityLogPage.xaml(.cs)` | Activity log ListView (converted from ActivityLogWindow) |
| `src/Nexus.App/NavTarget.cs` | Enum for tray-to-shell navigation |

### Modified Files

| File | Changes |
|------|---------|
| `src/Nexus.App/Nexus.App.csproj` | Add `Nexxor.Wpf.Ui` `ProjectReference`; bump `<Version>` 1.1.0 → 1.2.0 |
| `src/Nexus.App/App.xaml` | Merge `Nexxor.Wpf.Ui/Themes/Default.xaml` |
| `src/Nexus.App/App.xaml.cs` | Add `ShowMainWindow(NavTarget)`; remove `ShowSettingsWindow`, `ShowActivityLogWindow` |
| `src/Nexus.App/MainWindow.xaml` | Convert to `<ui:AppShellWindow>` with 3 NavItems |
| `src/Nexus.App/MainWindow.xaml.cs` | Base class `AppShellWindow`; keep singleton + hide-on-close; remove old state-panel code |
| `src/Nexus.App/Views/LoginWindow.xaml` | Convert to `<ui:OverlayLoginWindow>` with CardContent slot |
| `src/Nexus.App/Views/LoginWindow.xaml.cs` | Base class `OverlayLoginWindow`; preserve `LoginSucceeded` event |
| `src/Nexus.App/TrayIconManager.cs` | Settings/ActivityLog menu items call `ShowMainWindow(NavTarget.X)` instead of opening detached windows |
| `project_docs/structure.md` | Library in Key Dependencies; Views structure rewritten; date updated; path-block bug fixed |
| `CHANGELOG.md` | 1.2.0 entry |

### Deleted Files

| File | Reason |
|------|--------|
| `src/Nexus.App/Views/SettingsWindow.xaml(.cs)` | Replaced by `Views/Pages/SettingsPage.xaml(.cs)` |
| `src/Nexus.App/Views/ActivityLogWindow.xaml(.cs)` | Replaced by `Views/Pages/ActivityLogPage.xaml(.cs)` |

---

## Verification

1. `dotnet build src/Nexus.App/Nexus.App.csproj -c Release` succeeds with zero warnings.
2. Launch the app fresh (no saved token) → `OverlayLoginWindow` opens: borderless, 50% black backdrop, centered white card with email/password/URL fields. Login button is accent-styled. Login succeeds → window closes → tray icon appears.
3. Right-click tray → "Open Nexus" → `MainWindow` opens with sidebar (Home/Activity/Settings — Fluent icon glyphs, not emoji), Home selected by default, top header shows "Nexus" + user name + logout. State panel on Home shows current sync state. No empty middle gap, no vertical right-aligned button column.
4. Click "Sync Now" → status updates live on Home page; primary accent visible on the button; subsequent sync cycles update without reopening the window.
5. Click "Activity" in sidebar → `ActivityLogPage` loads in the content `Frame`; ListView columns aligned; filter dropdown still works.
6. Click "Settings" in sidebar → `SettingsPage` loads; sync interval combo + launch-on-startup checkbox + version display present and functional; Save button is accent-styled.
7. Right-click tray → "Settings" → `MainWindow` is brought to front AND sidebar selects Settings (does not open a second window).
8. Same for tray → "Activity Log".
9. Close MainWindow via X → window hides (does not close); tray still functions.
10. Logout from `TopRightNavBar` popup → MainWindow hides; `OverlayLoginWindow` appears.
11. Hover over any `NavButton` → background shifts (`AccentHover`); active item shows left accent bar.
12. Hover over primary buttons (Login, Sync Now, Save) → opacity drops; click → opacity drops further.
13. No hex color literals remain in any `.xaml` file under `src/Nexus.App/` — all colors come from library brushes via `StaticResource` or are absent (library defaults).
14. Velopack update path still works — install 1.1.0 from prior release, launch, check for updates, accept → upgrades to 1.2.0 without re-login.
15. Exit via tray → process terminates cleanly; no leaked window references.

---

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Library API churn while Nexus is the first consumer | Library plan keeps version 0.x explicitly because of this. Coordinate library changes through tech-lead until 1.0. |
| `OnClosing` override on `MainWindow` (hide instead of close) needs to work when inheriting `AppShellWindow` | `AppShellWindow` extends `Window` so overrides propagate. Verified in Step 3.6. |
| `OnClosing` flow on `OverlayLoginWindow` (Cancel button exits app) | Override `OnClosing` in the Nexus subclass; library doesn't need to know. Verified in Step 2.3. |
| Tray menu invoking `ShowMainWindow` rapidly opens duplicates if singleton check is wrong | Step 4.2's `_mainWindow is null \|\| !_mainWindow.IsLoaded` check + `Activate()` covers this. Verified in Step 4.4. |
| `SyncEngine` events fire when MainWindow is hidden — UI dispatcher exception risk | Existing code already handles this for the current MainWindow (per structure.md ordering invariant). `HomePage` subscribes/unsubscribes on `Loaded`/`Unloaded` so events don't reach a closed page. |
| Velopack delta size grows by library bundle (~1MB) | Acceptable — first-install only. Delta updates between 1.2.0+ don't re-ship the library. |
| ProjectReference path assumes both repos as siblings under `c:\xampp\htdocs\` — breaks on other dev machines or CI | Document the layout requirement in `project_docs/structure.md` Notes section. Promote to NuGet feed when JKS Desktop adopts the library. |
| Library Step 6 (demo consumer) hasn't run yet when this plan starts | Step 1.3 build gate halts the plan if the library reference fails. Library plan dependency stated explicitly in Context. |
| ActivityLogPage's ListView restyles differently under library's Inputs.xaml conventions | Step 5's screenshot review catches it. Scoped `<ListView.Resources>` fix if needed. |
| Page navigation via `PageUri` strings — typos compile but fail at runtime | Acceptable for v1.2.0. File `improvements.md` item for Type-safe variant once library exposes one. |

---

## Notes

1. **Repo layout requirement**: This plan assumes `c:\xampp\htdocs\nexus-windows-app\` and `c:\xampp\htdocs\nexxor-wpf-ui\` exist as siblings. Document in `structure.md` Notes once 6.1 lands.
2. **Library version pinning**: Use `ProjectReference` (no version pin needed). When library promotes to NuGet, pin the exact version in csproj.
3. **Custom brand accent**: Defer — Nexus uses library default `AccentBrush` (`#0078D4`). Override later in Nexus's `App.xaml` if PM wants distinct branding.
4. **`improvements.md` items to record at close-out**: (a) Brand-color accent override, (b) dark theme support when library adds it, (c) Type-safe `NavItem.PageType` (`Type` not `string`) when library exposes it.
5. **Release process unchanged**: `bash release.sh 1.2.0` still works — version bump in csproj is the only release-relevant change.
6. **`/push-dev` flow**: Plan handles the version bump + CHANGELOG itself (per `plan-main-window.md` precedent). `/push-dev` will detect the changes and push normally.
7. **C# control naming**: All new XAML elements use the Hungarian prefix per `conventions.md` (`btnSave`, `txtEmail`, `lblStatus`, `cmbInterval`, etc.).
