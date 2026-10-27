<#
  Teste da Fase 3 (rede) no PC real. Rode num PowerShell COMO ADMINISTRADOR:
      powershell -ExecutionPolicy Bypass -File tools\teste-rede.ps1

  Simula um jogo com uma cópia do ping.exe (fakegame.exe) e confere o que o Folga aplica e desfaz:
  política de QoS (limite de upload), Delivery Optimization (Windows Update) e o diário.
  Usa um config de teste separado; o seu config.json não é tocado.
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Abra o PowerShell como administrador e rode de novo." -ForegroundColor Yellow; exit 1
}
if (Get-Process Folga -ErrorAction SilentlyContinue) {
    Write-Host "Feche o Folga (menu da bandeja > Sair) antes do teste." -ForegroundColor Yellow; exit 1
}
$exe = @("$root\publish\Folga.exe", "$root\src\Folga.App\bin\Debug\net10.0-windows\Folga.exe", "$root\src\Folga.App\bin\Release\net10.0-windows\Folga.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { Write-Host "Folga.exe não encontrado; rode 'dotnet build' antes." -ForegroundColor Yellow; exit 1 }

$work = Join-Path $env:TEMP "folga-teste-rede"
New-Item -ItemType Directory -Force $work | Out-Null
$game = Join-Path $work "fakegame.exe"
Copy-Item "$env:windir\System32\PING.EXE" $game -Force
$cfg = Join-Path $work "config.json"
@'
{
  "version": 2,
  "boostEnabled": true,
  "games": [ { "name": "Jogo de teste", "executables": ["fakegame.exe"], "markDscp": true } ],
  "background": [ { "name": "Fundo de teste", "executables": ["PING.EXE"], "uploadLimitKbps": 500 } ],
  "memory": { "purgeStandbyList": false },
  "network": { "pauseWindowsUpdate": true }
}
'@ | Set-Content $cfg -Encoding utf8

function Policies { @("ActiveStore", "localhost") | ForEach-Object { $s = $_; Get-NetQosPolicy -PolicyStore $s -ErrorAction SilentlyContinue | Where-Object Name -like "Folga-*" | ForEach-Object { "$($_.Name) [$s]" } } }
function Check($ok, $text) { if ($ok) { Write-Host "  OK    $text" -ForegroundColor Green } else { Write-Host "  FALHA $text" -ForegroundColor Red; $script:failed++ } }
function WaitFor($condition, $seconds = 20) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }; return $false }
$failed = 0

$doBefore = (Get-Service DoSvc).Status
Write-Host "Delivery Optimization antes do teste: $doBefore"
$folga = Start-Process $exe -ArgumentList "--minimized --config `"$cfg`"" -PassThru
Start-Sleep -Seconds 4

Write-Host "`n1. Abrindo o jogo de teste"
$g = Start-Process $game -ArgumentList "-n 120 127.0.0.1" -WindowStyle Hidden -PassThru
Check (WaitFor { (Policies | Measure-Object).Count -ge 2 }) "políticas de QoS criadas: $((Policies) -join ', ')"
if ($doBefore -eq "Running") { Check (WaitFor { (Get-Service DoSvc).Status -ne "Running" }) "Delivery Optimization parado ($((Get-Service DoSvc).Status))" }
else { Write-Host "  --    Delivery Optimization já estava parado; pausa não testada" }

Write-Host "`n2. Fechando o jogo de teste"
$g | Stop-Process -Force
Check (WaitFor { (Policies | Measure-Object).Count -eq 0 }) "políticas de QoS removidas"
if ($doBefore -eq "Running") { Check (WaitFor { (Get-Service DoSvc).Status -eq "Running" }) "Delivery Optimization de volta ($((Get-Service DoSvc).Status))" }

Write-Host "`n3. Crash no meio do jogo"
$g = Start-Process $game -ArgumentList "-n 120 127.0.0.1" -WindowStyle Hidden -PassThru
WaitFor { (Policies | Measure-Object).Count -ge 2 } | Out-Null
$folga | Stop-Process -Force
$g | Stop-Process -Force
Check ((Policies | Measure-Object).Count -ge 2) "com o Folga morto, as políticas continuam (esperado)"
$folga = Start-Process $exe -ArgumentList "--minimized --config `"$cfg`"" -PassThru
Check (WaitFor { (Policies | Measure-Object).Count -eq 0 }) "ao reabrir, o diário removeu as políticas"
if ($doBefore -eq "Running") { Check (WaitFor { (Get-Service DoSvc).Status -eq "Running" }) "ao reabrir, o Delivery Optimization voltou" }

$folga | Stop-Process -Force
Write-Host "`nÚltimas linhas do log:"
Get-Content "$env:LOCALAPPDATA\Folga\logs\folga-$(Get-Date -Format yyyy-MM-dd).log" -Encoding utf8 -Tail 15 | ForEach-Object { "  $_" }
Write-Host ""
if ($failed -eq 0) { Write-Host "Tudo certo." -ForegroundColor Green } else { Write-Host "$failed verificações falharam; mande este resultado para o Claude." -ForegroundColor Red }
