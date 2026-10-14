# CertMonitor Deployment Script
# Önder AKÖZ

param(
    [string]$TargetPath = "C:\WEBROOT\SSLTLS_publish",
    [string]$AppPoolName = "CertMonitor"
)

Write-Host "=== CertMonitor Deployment Başlatılıyor ===" -ForegroundColor Cyan

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "HATA: Bu script yönetici olarak çalıştırılmalıdır!" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $TargetPath)) {
    Write-Host "UYARI: Hedef klasör bulunamadı: $TargetPath" -ForegroundColor Yellow
    $create = Read-Host "Oluşturulsun mu? (E/H)"
    if ($create -eq "E") {
        New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
        Write-Host "Klasör oluşturuldu." -ForegroundColor Green
    } else {
        Write-Host "İşlem iptal edildi." -ForegroundColor Red
        exit 1
    }
}

$appDataPath = Join-Path $TargetPath "App_Data"
$targetsPath = Join-Path $TargetPath "targets.csv"
$tempBackup = "C:\Temp\CertMonitor_Backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"

Write-Host "`n1. Mevcut veriler yedekleniyor..." -ForegroundColor Yellow

if (Test-Path $appDataPath) {
    New-Item -ItemType Directory -Path "$tempBackup\App_Data" -Force | Out-Null
    Copy-Item "$appDataPath\*" "$tempBackup\App_Data" -Recurse -Force
    Write-Host "   ✓ App_Data yedeklendi" -ForegroundColor Green
}

if (Test-Path $targetsPath) {
    Copy-Item $targetsPath "$tempBackup\targets.csv" -Force
    Write-Host "   ✓ targets.csv yedeklendi" -ForegroundColor Green
}

Write-Host "`n2. IIS Application Pool durduruluyor..." -ForegroundColor Yellow
try {
    Import-Module WebAdministration -ErrorAction Stop
    
    if (Test-Path "IIS:\AppPools\$AppPoolName") {
        $state = (Get-WebAppPoolState -Name $AppPoolName).Value
        if ($state -ne "Stopped") {
            Stop-WebAppPool -Name $AppPoolName
            Start-Sleep -Seconds 3
            Write-Host "   ✓ Application Pool durduruldu" -ForegroundColor Green
        } else {
            Write-Host "   ✓ Application Pool zaten durdurulmuş" -ForegroundColor Green
        }
    } else {
        Write-Host "    Application Pool bulunamadı: $AppPoolName" -ForegroundColor Yellow
    }
} catch {
    Write-Host "    IIS modülü yüklenemedi veya pool durdurulamadı: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "   Manuel olarak durdurun veya devam etmek için Enter'a basın..." -ForegroundColor Yellow
    Read-Host
}
Write-Host "   Dosyalar serbest bırakılıyor (5 saniye)..." -ForegroundColor Gray
Start-Sleep -Seconds 5

Write-Host "`n3. Proje publish ediliyor..." -ForegroundColor Yellow
$publishResult = dotnet publish -c Release -o $TargetPath 2>&1

if ($LASTEXITCODE -eq 0) {
    Write-Host "    Publish başarılı" -ForegroundColor Green
} else {
    Write-Host "    Publish başarısız!" -ForegroundColor Red
    Write-Host $publishResult
    Write-Host "`nYedek geri yükleniyor..." -ForegroundColor Yellow
    
    if (Test-Path "$tempBackup\App_Data") {
        Copy-Item "$tempBackup\App_Data\*" $appDataPath -Recurse -Force
    }
    if (Test-Path "$tempBackup\targets.csv") {
        Copy-Item "$tempBackup\targets.csv" $targetsPath -Force
    }
    
    # IIS'i başlat
    if (Test-Path "IIS:\AppPools\$AppPoolName") {
        Start-WebAppPool -Name $AppPoolName
    }
    
    exit 1
}

Write-Host "`n4. Veriler geri yükleniyor..." -ForegroundColor Yellow

if (Test-Path "$tempBackup\App_Data") {
    if (-not (Test-Path $appDataPath)) {
        New-Item -ItemType Directory -Path $appDataPath -Force | Out-Null
    }
    Copy-Item "$tempBackup\App_Data\*" $appDataPath -Recurse -Force
    Write-Host "   ✓ App_Data geri yüklendi" -ForegroundColor Green
}

if (Test-Path "$tempBackup\targets.csv") {
    Copy-Item "$tempBackup\targets.csv" $targetsPath -Force
    Write-Host "   ✓ targets.csv geri yüklendi" -ForegroundColor Green
}

Write-Host "`n5. IIS Application Pool başlatılıyor..." -ForegroundColor Yellow
try {
    if (Test-Path "IIS:\AppPools\$AppPoolName") {
        Start-WebAppPool -Name $AppPoolName
        Start-Sleep -Seconds 2
        Write-Host "   ✓ Application Pool başlatıldı" -ForegroundColor Green
    }
} catch {
    Write-Host "    Application Pool başlatılamadı: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`n6. IIS yenileniyor..." -ForegroundColor Yellow
iisreset /noforce | Out-Null
Write-Host "   ✓ IIS yenilendi" -ForegroundColor Green

Write-Host "`n7. Geçici dosyalar temizleniyor..." -ForegroundColor Yellow
Start-Sleep -Seconds 2
Remove-Item $tempBackup -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "   ✓ Temizlendi" -ForegroundColor Green

Write-Host "`n=== Deployment Tamamlandı! ===" -ForegroundColor Green
Write-Host "`nUygulama çalışıyor: $TargetPath" -ForegroundColor Cyan
Write-Host "Log dosyaları: $appDataPath" -ForegroundColor Gray

Write-Host "`nUygulama test ediliyor..." -ForegroundColor Yellow
Start-Sleep -Seconds 3

try {
    $testUrl = "http://localhost"  
	
    $response = Invoke-WebRequest -Uri $testUrl -Method Head -TimeoutSec 5 -ErrorAction Stop
    if ($response.StatusCode -eq 200 -or $response.StatusCode -eq 302) {
        Write-Host "✓ Uygulama erişilebilir durumda!" -ForegroundColor Green
    }
} catch {
    Write-Host " Uygulama test edilemedi. Manuel kontrol edin: $testUrl" -ForegroundColor Yellow
}

Write-Host "`nDeployment başarıyla tamamlandı! " -ForegroundColor Green
