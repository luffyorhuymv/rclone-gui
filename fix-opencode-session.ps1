param(
    [string]$Drive = "X:",
    [string]$SubPath = "public_html"
)

$ErrorActionPreference = "Stop"

function Normalize-PathKey([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return "" }
    return $PathValue.Replace("/", "\").TrimEnd("\").ToLowerInvariant()
}

function Path-Leaf([string]$PathValue) {
    $key = Normalize-PathKey $PathValue
    if ([string]::IsNullOrWhiteSpace($key)) { return "" }
    return Split-Path -Leaf $key
}

function Ensure-Object($Parent, [string]$Name) {
    $prop = $Parent.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value) {
        $value = [pscustomobject]@{}
        if ($null -eq $prop) {
            $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue $value
        } else {
            $Parent.$Name = $value
        }
    }
    return $Parent.$Name
}

function Set-Prop($Parent, [string]$Name, $Value) {
    $prop = $Parent.PSObject.Properties[$Name]
    if ($null -eq $prop) {
        $Parent | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    } else {
        $Parent.$Name = $Value
    }
}

function Read-JsonSlot($Root, [string]$Name) {
    $prop = $Root.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value) {
        return [pscustomobject]@{}
    }
    if ($prop.Value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($prop.Value)) { return [pscustomobject]@{} }
        return $prop.Value | ConvertFrom-Json
    }
    return $prop.Value
}

$Drive = $Drive.Trim()
if (-not $Drive.EndsWith(":")) { $Drive = "${Drive}:" }

$disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$Drive'" -ErrorAction SilentlyContinue
$provider = $disk.ProviderName
if (-not [string]::IsNullOrWhiteSpace($provider) -and $provider.StartsWith("\\")) {
    $projectDir = Join-Path $provider.TrimEnd("\") $SubPath
} else {
    $projectDir = Join-Path "$Drive\" $SubPath
}
$projectDir = [System.IO.Path]::GetFullPath($projectDir).TrimEnd("\")
if ($projectDir -match '^[A-Z]:$' -or $projectDir -match '^\\\\[^\\]+\\[^\\]+$') {
    throw "Do not open the mount root directly. Pass a project folder, for example: fix-opencode-session.bat X: public_html"
}

$stateFile = Join-Path $env:APPDATA "ai.opencode.desktop\opencode.global.dat"
if (-not (Test-Path $stateFile)) {
    throw "OpenCode state file not found: $stateFile"
}

Get-Process OpenCode -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

if (-not (Test-Path (Join-Path $projectDir ".git"))) {
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($git) {
        & git -C $projectDir init -b main | Out-Null
        if ($LASTEXITCODE -eq 0) {
            & git -C $projectDir config user.name "RcloneDrive" | Out-Null
            & git -C $projectDir config user.email "rclonedrive@local" | Out-Null
            & git -C $projectDir commit --allow-empty -m "Initialize repository" | Out-Null
        }
    } else {
        Write-Warning "git.exe not found. Install Git for Windows if OpenCode still does not create sessions."
    }
}

$state = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
$server = Read-JsonSlot $state "server"
$page = Read-JsonSlot $state "layout.page"
$projects = Ensure-Object $server "projects"
$sessions = Ensure-Object $page "lastProjectSession"
$lastProject = Ensure-Object $server "lastProject"

Set-Prop $lastProject "local" $projectDir

$existingProjects = @()
if ($null -ne $projects.local) { $existingProjects = @($projects.local) }
$projectKey = Normalize-PathKey $projectDir
$filteredProjects = @($existingProjects | Where-Object {
    $itemPath = $_.worktree
    if ([string]::IsNullOrWhiteSpace($itemPath)) { return $true }
    return (Normalize-PathKey $itemPath) -ne $projectKey
})
$newProject = [pscustomobject]@{ worktree = $projectDir; expanded = $true }
Set-Prop $projects "local" (@($newProject) + $filteredProjects)

$candidate = $null
$targetLeaf = Path-Leaf $projectDir
foreach ($prop in $sessions.PSObject.Properties) {
    $sessionDir = $null
    if ($null -ne $prop.Value -and $null -ne $prop.Value.PSObject.Properties["directory"]) {
        $sessionDir = [string]$prop.Value.directory
    }
    if ((Normalize-PathKey $prop.Name) -eq $projectKey -or (Normalize-PathKey $sessionDir) -eq $projectKey) {
        $candidate = $prop.Value
        break
    }
    if ($targetLeaf -ne "" -and ((Path-Leaf $prop.Name) -eq $targetLeaf -or (Path-Leaf $sessionDir) -eq $targetLeaf)) {
        $candidate = $prop.Value
    }
}

if ($null -ne $candidate) {
    Set-Prop $candidate "directory" $projectDir
    Set-Prop $candidate "at" ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
    Set-Prop $sessions $projectDir $candidate
}

Set-Prop $state "server" ($server | ConvertTo-Json -Depth 100 -Compress)
Set-Prop $state "layout.page" ($page | ConvertTo-Json -Depth 100 -Compress)
Copy-Item -LiteralPath $stateFile -Destination "$stateFile.bak-rclone-$(Get-Date -Format yyyyMMddHHmmss)" -Force
$json = $state | ConvertTo-Json -Depth 100
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($stateFile, $json, $utf8NoBom)

$openCodeExe = Join-Path $env:LOCALAPPDATA "Programs\@opencode-aidesktop\OpenCode.exe"
$link = "opencode://open-project?directory=$([System.Uri]::EscapeDataString($projectDir))"
if (Test-Path $openCodeExe) {
    Start-Process -FilePath $openCodeExe -ArgumentList $link
} else {
    Start-Process $link
}

Write-Host "OpenCode fixed project path:"
Write-Host $projectDir
