# Deploy AI Content Studio into the ai-content namespace.
# Never targets another namespace and never prints secret values.
# Example: .\scripts\deploy-k8s.ps1 -Tag v1.0.0 -Context my-cluster
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Context = $env:K8S_CONTEXT,
    [string]$Username = $env:DOCKERHUB_USERNAME,
    [string]$Prefix = $(if ($env:DOCKERHUB_IMAGE_PREFIX) { $env:DOCKERHUB_IMAGE_PREFIX } else { "ai-content" }),
    [string]$HostName = $(if ($env:K8S_INGRESS_HOST) { $env:K8S_INGRESS_HOST } else { "studio.example.com" }),
    [string]$PullSecret = "",
    [switch]$WithDataServices,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$Namespace = "ai-content"
if (-not $Context -or -not $Username) { throw "Required: -Context and a Docker Hub username" }
if ($Namespace -ne "ai-content") { throw "Refusing to touch a namespace other than ai-content" }
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) { throw "kubectl is not installed" }

Write-Output "Target context: $Context"
kubectl --context $Context config current-context
if ($LASTEXITCODE -ne 0) { throw "context $Context is not usable" }
kubectl --context $Context cluster-info
if ($LASTEXITCODE -ne 0) { throw "cluster is not reachable" }
kubectl --context $Context auth can-i create namespace | Out-Null
kubectl --context $Context auth can-i create deployment -n $Namespace | Out-Null

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ai-content-" + [guid]::NewGuid().ToString("N"))
Copy-Item -Recurse (Join-Path $root "deploy/k8s") $work
Get-ChildItem $work -Recurse -Filter *.yaml | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    $text = $text.Replace("__REGISTRY__/ai-content-", "$Username/$Prefix-").Replace("__TAG__", $Tag).Replace("studio.example.com", $HostName)
    if ($PullSecret -and $_.Name -eq "workloads.yaml") {
        $text = $text.Replace("serviceAccountName: ai-content", "serviceAccountName: ai-content`n      imagePullSecrets:`n        - { name: $PullSecret }")
    }
    Set-Content -Path $_.FullName -Value $text -NoNewline
}
Write-Output "Namespace: $Namespace"
Write-Output "Images: $Username/${Prefix}-{web,api,agent-worker,video-worker}:$Tag"
Write-Output "Ingress host: $HostName"
if ($DryRun) {
    kubectl --context $Context apply --dry-run=client -f (Join-Path $work "namespace.yaml")
    kubectl --context $Context apply --dry-run=client -k $work
    if ($WithDataServices) { kubectl --context $Context apply --dry-run=client -f (Join-Path $work "data") }
    if ($LASTEXITCODE -ne 0) { throw "dry run failed" }
    Write-Output "Dry run only. The cluster was not changed."
    return
}
kubectl --context $Context apply -f (Join-Path $work "namespace.yaml")
if ($LASTEXITCODE -ne 0) { throw "namespace apply failed" }
$required = @("JWT_SIGNING_KEY", "AI_API_KEY", "ENGINE_INTERNAL_TOKEN", "DATABASE_URL", "REDIS_URL")
foreach ($key in $required) {
    kubectl --context $Context -n $Namespace get secret ai-content-secrets -o "jsonpath={.data.$key}" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Secret ai-content-secrets is missing $key. See deploy/k8s/secret.example.yaml" }
}
kubectl --context $Context apply -k $work
if ($LASTEXITCODE -ne 0) { throw "apply failed" }
if ($WithDataServices) { kubectl --context $Context apply -f (Join-Path $work "data") }
foreach ($deploy in @("ai-content-api", "ai-content-web", "ai-content-agent-worker", "ai-content-video")) {
    kubectl --context $Context -n $Namespace rollout status "deployment/$deploy" --timeout=240s
    if ($LASTEXITCODE -ne 0) { throw "rollout failed for $deploy. kubectl --context $Context -n $Namespace describe pods" }
}
kubectl --context $Context -n $Namespace get pods,svc,ingress
Write-Output "Logs: kubectl --context $Context -n $Namespace logs deploy/ai-content-agent-worker"
