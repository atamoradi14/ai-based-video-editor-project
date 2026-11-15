# Build and push the four AI Content Studio images.
# Does not embed credentials. Run `docker login` first.
# Example: .\scripts\docker-build-push.ps1 -Tag v1.0.0
[CmdletBinding()]
param(
    [string]$Username = $env:DOCKERHUB_USERNAME,
    [string]$Prefix = $(if ($env:DOCKERHUB_IMAGE_PREFIX) { $env:DOCKERHUB_IMAGE_PREFIX } else { "ai-content" }),
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Platform = "linux/amd64",
    [switch]$Latest,
    [ValidateSet("all", "web", "api", "agent-worker", "video-worker")][string]$Service = "all",
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
if (-not $Username) { throw "Missing -Username or DOCKERHUB_USERNAME" }
if ($Tag -eq "latest") { throw "Refusing to use latest as the only tag" }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker is not installed" }
docker buildx version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker buildx is not available" }
if (-not $DryRun) { docker info | Out-Null; if ($LASTEXITCODE -ne 0) { throw "docker daemon is not reachable" } }

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$images = [ordered]@{
    "web" = "src/frontend/Dockerfile"
    "api" = "src/backend/AiContent.Api/Dockerfile"
    "agent-worker" = "src/backend/AiContent.AgentWorker/Dockerfile"
    "video-worker" = "video-engine/Dockerfile"
}
$selected = @()
if ($Service -eq "all") { $selected = @($images.Keys) } else { $selected = @($Service) }
foreach ($name in $selected) {
    $repo = if ($name -eq "video-worker") { "$Prefix-video-worker" } else { "$Prefix-$name" }
    $image = "$Username/$repo"
    $args = @("buildx", "build", "--platform", $Platform, "-f", (Join-Path $root $images[$name]), "-t", "${image}:${Tag}")
    if ($Latest) { $args += @("-t", "${image}:latest") }
    $args += $root
    if (-not $DryRun) { $args += "--push" }
    Write-Output "image ${image}:${Tag}"
    Write-Output (" + docker " + ($args -join " "))
    if (-not $DryRun) {
        & docker @args
        if ($LASTEXITCODE -ne 0) { throw "build failed for $image" }
        docker buildx imagetools inspect "${image}:${Tag}"
        if ($LASTEXITCODE -ne 0) { throw "could not read the digest for $image" }
    }
}
if ($DryRun) { Write-Output "Dry run only. Nothing was built or pushed. docker login is required before a real push." }
