# Expand every Campaign Evolved scenario biped/vehicle/weapon/character palette
# and dedicated hm_ally / hm_hostile squads into MMYJ_FULL_VEHI_WAP_P, then
# write Superior Marines/Covenant [char] AI as the independent MMYJ_FULL_CHAR_P
# overlay. Engine palette limits: objects 256 entries and characters 64.
param(
    [string]$Paks,
    [string]$Output = (Join-Path $PSScriptRoot "out\MMYJ_FULL_VEHI_WAP_P.utoc"),
    [switch]$DryRun,
    [switch]$Install,
    [switch]$UpdateBundledAssets
)

$ErrorActionPreference = "Stop"

function Resolve-PaksDirectory {
    param([string]$Explicit)
    if ($Explicit) {
        $full = (Resolve-Path $Explicit).Path
        if (-not (Get-ChildItem $full -Filter *.utoc -ErrorAction SilentlyContinue)) {
            throw "No .utoc files in $full"
        }
        return $full
    }
    if ($env:HALO_CAMPAIGN_EVOLVED_ROOT) {
        $candidates = @(
            (Join-Path $env:HALO_CAMPAIGN_EVOLVED_ROOT "Meteorite\Content\Paks"),
            (Join-Path $env:HALO_CAMPAIGN_EVOLVED_ROOT "Content\Meteorite\Content\Paks")
        )
        foreach ($candidate in $candidates) {
            if ((Test-Path $candidate) -and (Get-ChildItem $candidate -Filter *.utoc -ErrorAction SilentlyContinue)) {
                return (Resolve-Path $candidate).Path
            }
        }
    }
    $roots = Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -match 'Halo.*Campaign Evolved' -and $props.InstallLocation) {
                $props.InstallLocation
            }
        }
    foreach ($root in $roots) {
        $candidate = Join-Path $root "Meteorite\Content\Paks"
        if ((Test-Path $candidate) -and (Get-ChildItem $candidate -Filter *.utoc -ErrorAction SilentlyContinue)) {
            return (Resolve-Path $candidate).Path
        }
    }
    throw "Could not find Meteorite\Content\Paks. Pass -Paks or set HALO_CAMPAIGN_EVOLVED_ROOT."
}

function Remove-OverlayStem {
    param([string]$PaksDir, [string]$Stem)
    foreach ($ext in @(".utoc", ".ucas", ".pak")) {
        $path = Join-Path $PaksDir ($Stem + $ext)
        if (Test-Path $path) {
            Remove-Item -Force $path
            Write-Host "Removed $path"
        }
    }
}

$exeCandidates = @(
    (Join-Path $PSScriptRoot "target\release\halomeister-tagmod-exporter.exe"),
    (Join-Path $PSScriptRoot "..\..\src\HaloMeister.App\Assets\Native\halomeister-tagmod-exporter.exe")
)
$exe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    Push-Location $PSScriptRoot
    try { cargo build --release }
    finally { Pop-Location }
    $exe = Join-Path $PSScriptRoot "target\release\halomeister-tagmod-exporter.exe"
}
if (-not (Test-Path $exe)) {
    throw "halomeister-tagmod-exporter.exe was not found. Build native/HaloMeister.TagModExporter first."
}

$paksDir = Resolve-PaksDirectory -Explicit $Paks
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null

# Build from base + non-conflicting packs: drop both previous scenario overlays
# so the merged package is not reading its own prior output.
if (-not $DryRun) {
    if (Get-Process -Name "HaloCampaignEvolved" -ErrorAction SilentlyContinue) {
        throw "Close Halo: Campaign Evolved before rebuilding/installing the overlay."
    }
    Remove-OverlayStem -PaksDir $paksDir -Stem "ZZ_HM_DemoSquads_P"
    Remove-OverlayStem -PaksDir $paksDir -Stem "HM_DemoSquads_P"
    Remove-OverlayStem -PaksDir $paksDir -Stem "MMYJ_FULL_VEHI_WAP_P"
    Remove-OverlayStem -PaksDir $paksDir -Stem "MMYJ_FULL_CHAR_P"
    Remove-OverlayStem -PaksDir $paksDir -Stem "HM_FullPalettes_P"
}

function Update-BundledFingerprint {
    param(
        [string]$ServicePath,
        [string]$ConstantName,
        [string]$UtocPath
    )
    $fingerprintParts = @()
    $bundleDir = Join-Path $PSScriptRoot "..\..\src\HaloMeister.App\Assets\Overlays"
    New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
    foreach ($ext in @(".utoc", ".ucas", ".pak")) {
        $source = [IO.Path]::ChangeExtension($UtocPath, $ext)
        if (-not (Test-Path $source)) { throw "Missing $source" }
        $destination = Join-Path $bundleDir (Split-Path $source -Leaf)
        Copy-Item -Force $source $destination
        Write-Host "Updated bundled asset $destination"
        $bytes = [IO.File]::ReadAllBytes($destination)
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
        }
        finally {
            $sha.Dispose()
        }
        $fingerprintParts += "$($bytes.Length):$hash"
    }

    $fingerprint = $fingerprintParts -join "|"
    $serviceText = [IO.File]::ReadAllText($ServicePath)
    $replacement = "public const string $ConstantName =`r`n        `"$fingerprint`";"
    $updated = [regex]::Replace(
        $serviceText,
        "public const string $ConstantName\s*=\s*`"[^`"]*`";",
        $replacement,
        1)
    if ($updated -eq $serviceText) {
        if ($serviceText -match [regex]::Escape("public const string $ConstantName") -and
            $serviceText.Contains($fingerprint)) {
            Write-Host "$ConstantName already matches; fingerprint unchanged."
            return
        }
        throw "Failed to rewrite $ConstantName in FullPalettesOverlayService.cs"
    }
    [IO.File]::WriteAllText($ServicePath, $updated)
    Write-Host "Updated $ConstantName in FullPalettesOverlayService.cs"
    Write-Host "Fingerprint: $fingerprint"
}

$campaignArgs = @("--paks", $paksDir, "--expand-palettes", "--output", $Output)
if ($DryRun) { $campaignArgs += "--dry-run" }
& $exe @campaignArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$characterOutput = Join-Path (Split-Path $Output) "MMYJ_FULL_CHAR_P.utoc"
$characterArgs = @("--paks", $paksDir, "--expand-characters", "--output", $characterOutput)
if ($DryRun) { $characterArgs += "--dry-run" }
& $exe @characterArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($UpdateBundledAssets -and -not $DryRun) {
    $servicePath = Join-Path $PSScriptRoot `
        "..\..\src\HaloMeister.App\Services\FullPalettesOverlayService.cs"
    if (-not (Test-Path $servicePath)) {
        throw "Missing FullPalettesOverlayService.cs for fingerprint update."
    }
    Update-BundledFingerprint -ServicePath $servicePath `
        -ConstantName "ExpectedBundledFingerprint" -UtocPath $Output
    Update-BundledFingerprint -ServicePath $servicePath `
        -ConstantName "ExpectedCharacterFingerprint" -UtocPath $characterOutput
}

if ($Install -and -not $DryRun) {
    foreach ($utoc in @($Output, $characterOutput)) {
        foreach ($ext in @(".utoc", ".ucas", ".pak")) {
            $source = [IO.Path]::ChangeExtension($utoc, $ext)
            $destination = Join-Path $paksDir (Split-Path $source -Leaf)
            if (-not (Test-Path $source)) { throw "Missing $source" }
            Copy-Item -Force $source $destination
            Write-Host "Installed $destination"
        }
    }
    # Belt-and-suspenders: never leave the old dual-pack stem around.
    Remove-OverlayStem -PaksDir $paksDir -Stem "ZZ_HM_DemoSquads_P"
    Remove-OverlayStem -PaksDir $paksDir -Stem "HM_DemoSquads_P"
    Write-Host "Validating dedicated hm_ally/hm_hostile scaffolds across all scenarios..."
    & $exe --paks $paksDir --dump-demo-squads
    if ($LASTEXITCODE -ne 0) {
        throw "Dedicated scaffold validation failed after install."
    }
    Write-Host "Restart the game so MMYJ_FULL_VEHI_WAP_P and MMYJ_FULL_CHAR_P mount."
}
