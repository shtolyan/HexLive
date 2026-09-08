#Requires -RunAsAdministrator

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PublicKey = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICwLphO6ubHkWfv7TYvzZVaGzF1DU4toAJXc1WRMpZoG natepo4ty@gmail.com'
$TailscaleNetwork = '100.64.0.0/10'

Write-Host 'HexLive MSI SSH setup' -ForegroundColor Cyan
Write-Host 'Installing Windows OpenSSH Server if needed...'

$Capability = Get-WindowsCapability -Online |
    Where-Object { $_.Name -like 'OpenSSH.Server*' } |
    Select-Object -First 1

if ($null -eq $Capability) {
    throw 'Windows OpenSSH Server capability was not found. Install current Windows updates and run this file again.'
}

if ($Capability.State -ne 'Installed') {
    Add-WindowsCapability -Online -Name $Capability.Name | Out-Null
}

$SshDirectory = Join-Path $env:ProgramData 'ssh'
$AuthorizedKeys = Join-Path $SshDirectory 'administrators_authorized_keys'

New-Item -ItemType Directory -Path $SshDirectory -Force | Out-Null
if (-not (Test-Path $AuthorizedKeys)) {
    New-Item -ItemType File -Path $AuthorizedKeys -Force | Out-Null
}

$ExistingKeys = Get-Content -Path $AuthorizedKeys -Raw -ErrorAction SilentlyContinue
if ($null -eq $ExistingKeys -or -not $ExistingKeys.Contains($PublicKey)) {
    Add-Content -Path $AuthorizedKeys -Value $PublicKey -Encoding ascii
}

# Windows OpenSSH requires this exact ACL for administrator keys. SIDs keep the
# command independent of the Windows display language.
& icacls.exe $AuthorizedKeys /inheritance:r | Out-Null
& icacls.exe $AuthorizedKeys /grant '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not secure $AuthorizedKeys"
}

Set-Service -Name sshd -StartupType Automatic

$FirewallRule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if ($null -ne $FirewallRule) {
    $FirewallRule | Set-NetFirewallRule -Enabled True -Profile Any
    $FirewallRule |
        Get-NetFirewallAddressFilter |
        Set-NetFirewallAddressFilter -RemoteAddress $TailscaleNetwork
} else {
    $FirewallRule = Get-NetFirewallRule -Name 'HexLive-SSHD-Tailscale' -ErrorAction SilentlyContinue
    if ($null -eq $FirewallRule) {
        New-NetFirewallRule `
            -Name 'HexLive-SSHD-Tailscale' `
            -DisplayName 'HexLive SSH via Tailscale' `
            -Direction Inbound `
            -Action Allow `
            -Protocol TCP `
            -LocalPort 22 `
            -RemoteAddress $TailscaleNetwork `
            -Profile Any | Out-Null
    } else {
        $FirewallRule | Set-NetFirewallRule -Enabled True -Profile Any
        $FirewallRule |
            Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress $TailscaleNetwork
    }
}

Start-Service -Name sshd
Restart-Service -Name sshd

$Service = Get-Service -Name sshd
$Listener = Get-NetTCPConnection -LocalPort 22 -State Listen -ErrorAction SilentlyContinue
if ($Service.Status -ne 'Running' -or $null -eq $Listener) {
    throw 'sshd did not start listening on TCP port 22.'
}

Write-Host ''
Write-Host 'SUCCESS: MSI is ready for HexLive SSH deployment.' -ForegroundColor Green
Write-Host "Windows user: $env:USERNAME"
Write-Host "Computer:     $env:COMPUTERNAME"
Write-Host "sshd:         $($Service.Status)"
Write-Host 'TCP port:     22 (Tailscale only)'
Write-Host ''
Write-Host 'Send the four lines above back to the Mac agent. Do not send passwords or private keys.'
