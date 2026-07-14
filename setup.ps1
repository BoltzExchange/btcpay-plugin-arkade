# Check if not in a CI environment
if (-not (Test-Path Env:CI)) {
    # Initialize the server submodule
    Write-Host "Initializing and updating submodules..."
    git submodule init
    if ($LASTEXITCODE -eq 0) {
        git submodule update --recursive
    } else {
        Write-Error "git submodule init failed."
        exit 1
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "git submodule update --recursive failed."
        exit 1
    }

    # Install the workloads
    Write-Host "Restoring dotnet workloads..."
    dotnet workload restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet workload restore failed."
        exit 1
    }
}

# Publish plugin (NNark dependencies are included via ProjectReferences)
$root = Get-Location
$pluginDir = "BTCPayServer.Plugins.Boltz.Arkade"
$publishDir = Join-Path $root "$pluginDir/bin/Debug/net10.0"

# Remove old build artifacts
if (Test-Path $publishDir) {
    Write-Host "Cleaning $publishDir..."
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "Publishing plugin (includes NNark dependencies)..."
dotnet publish $pluginDir -c Debug -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

# Register the plugin with the dev server (and the E2E test bin, if built)
# via DEBUG_PLUGINS in appsettings.dev.json — same as `make appsettings`.
Write-Host "Writing appsettings.dev.json via ConfigBuilder..."
dotnet run --project ConfigBuilder/ConfigBuilder.csproj
if ($LASTEXITCODE -ne 0) {
    Write-Error "ConfigBuilder failed."
    exit 1
}

Write-Host "Setup complete."
