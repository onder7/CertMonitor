# CertMonitor – SSL/TLS Sertifika İzleme Sistemi

**CertMonitor**, IIS üzerinde çalışan ASP.NET Core 8 tabanlı, kurumsal SSL/TLS sertifika izleme ve yönetim platformudur. Arka planda periyodik olarak hedef sunucuların sertifikalarını tarar, süre sonu uyarıları gönderir ve web dashboard'da gerçek zamanlı izleme sağlar.

## ✨ Özellikler

- 🔍 **Otomatik Sertifika Tarama** - Çoklu sunucuları periyodik olarak tarama (varsayılan: 6 saatte bir)
- 📊 **Web Dashboard** - Gerçek zamanlı sertifika durumu, süre sonu takibi, detaylı raporlar
- 📧 **E-posta Bildirimleri** - SMTP üzerinden otomatik süre sonu uyarıları (Warning, Critical, Expired)
- 🌐 **IP Adresi Tespiti** - Hostname'den otomatik IP çözümleme (IPv4/IPv6 desteği)
- 🔐 **Güvenli Kimlik Doğrulama** - PBKDF2 (SHA-256, 120.000 iterasyon) ile şifrelenmiş oturum yönetimi
- ⚙️ **Web Tabanlı Ayarlar** - SMTP ve bildirim ayarları web arayüzünden yönetilebilir
- 📁 **CSV Export** - Sertifika raporlarını CSV formatında dışa aktarma
- 🎨 **Modern UI** - Responsive, kullanıcı dostu arayüz

## 🔍 Tarama Özellikleri

- ✅ SSL/TLS sertifika geçerlilik kontrolü
- ✅ Hostname/SAN doğrulaması (wildcard desteği)
- ✅ Sertifika zinciri validasyonu
- ✅ Kalan geçerlilik süresi hesaplama
- ✅ TLS protokol versiyonu tespiti (TLS 1.0 - 1.3)
- ✅ Anahtar algoritması ve güç analizi (RSA/ECDSA)
- ✅ Self-signed sertifika tespiti
- ✅ Sertifika yenileme takibi
- ⚠️ Zayıf anahtar uyarıları (RSA < 2048 bit, SHA-1 imza)

## 📋 Gereksinimler

- **Sunucu:** Windows Server (IIS 10+)
- **.NET:** .NET 8 Runtime (Hosting Bundle)
- **Veritabanı:** Yok (JSON file-based)
- **SMTP:** İsteğe bağlı (e-posta bildirimleri için)

## 🚀 Kurulum

### 1. Projeyi Yayınlama
```powershell
dotnet publish -c Release -o C:\inetpub\CertMonitor
```

### 2. .NET 8 Hosting Bundle Kurulumu
[İndir](https://dotnet.microsoft.com/download/dotnet/8.0) ve `iisreset` çalıştır.

### 3. IIS Yapılandırması
```powershell
Import-Module WebAdministration

# Application Pool oluştur
New-WebAppPool -Name CertMonitor
Set-ItemProperty IIS:\AppPools\CertMonitor managedRuntimeVersion ""
Set-ItemProperty IIS:\AppPools\CertMonitor startMode AlwaysRunning
Set-ItemProperty IIS:\AppPools\CertMonitor processModel.idleTimeout 00:00:00

# Site oluştur
New-Website -Name CertMonitor -Port 8080 `
    -PhysicalPath C:\inetpub\CertMonitor `
    -ApplicationPool CertMonitor

# Preload etkinleştir (arka plan taraması için)
Set-ItemProperty IIS:\Sites\CertMonitor applicationDefaults.preloadEnabled $true

# App_Data izinleri
icacls C:\inetpub\CertMonitor\App_Data /grant "IIS AppPool\CertMonitor:(OI)(CI)M"
```

### 4. İlk Giriş
- **URL:** http://localhost:8080
- **Kullanıcı:** admin
- **Parola:** `appsettings.json`'dan değiştirin!

## ⚙️ Yapılandırma

### appsettings.json

```json
{
  "Auth": {
    "AdminUser": "admin",
    "AdminPassword": "ChangeMe123!",
    "SessionHours": 8
  },
  "Scan": {
    "IntervalHours": 6,
    "MaxParallel": 10,
    "TimeoutSeconds": 10,
    "TargetsFile": "targets.csv",
    "WarningDays": 30,
    "CriticalDays": 7
  },
  "Email": {
    "Enabled": false,
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "Username": "",
    "Password": "",
    "FromAddress": "certmonitor@example.com",
    "ToAddresses": "admin@example.com",
    "NotifyDaysBefore": 30
  }
}
```

### targets.csv

```csv
Host,Port,SniName,Environment,Owner,Enabled
www.example.com,443,,Production,IT Team,true
api.example.com,8443,api.example.com,Production,DevOps,true
intranet.local,443,,Internal,Infrastructure,true
```

## 📧 E-posta Bildirimleri

### SMTP Ayarları (Web Arayüzünden)

1. **https://yoursite/settings.html** adresine gidin
2. SMTP ayarlarını yapılandırın:
   - SMTP Sunucu, Port, SSL/TLS
   - Kullanıcı adı ve parola
   - Gönderen/Alıcı adresleri
   - Bildirim kuralları (X gün öncesinden uyarı)
3. **Test E-postası** göndererek doğrulayın
4. **Kaydet** - Ayarlar `App_Data/email-config.json`'a kaydedilir

### Bildirim Örneği

```
Konu: SSL Sertifika Uyarısı - 3 sertifika dikkat gerektiriyor

Host                 Port  Durum     Kalan Gün  Bitiş
──────────────────────────────────────────────────────
expired.example.com  443   Expired   -5         2026-10-08
urgent.example.com   443   Critical  3          2026-10-16
warning.example.com  443   Warning   25         2026-11-07
```

## 🎨 Ekran Görüntüleri

### Dashboard
- Sertifika durumu kartları (Toplam, OK, Warning, Critical, Expired)
- Detaylı tablo (Host, IP, Port, Ortam, Kalan Gün, TLS versiyon)
- Durum bazlı filtreleme
- CSV export

### Ayarlar Sayfası
- SMTP yapılandırma
- Test e-postası gönderme
- Bildirim kuralları

## 🔒 Güvenlik

- **Kimlik Doğrulama:** PBKDF2 (SHA-256, 120.000 iterasyon)
- **Oturum Yönetimi:** HttpOnly, SameSite=Strict cookie
- **Parola Saklama:** Encrypted hash (düz metin yok)
- **Dosya İzinleri:** IIS app pool izin kontrolü
- **XSS Koruması:** HTML encoding

## 📊 Performans

- **Paralel Tarama:** 10 hedef aynı anda (ayarlanabilir)
- **Timeout:** 10 saniye bağlantı timeout'u
- **In-Memory Cache:** Hızlı sonuç erişimi
- **Persistence:** JSON file-based (restart dayanıklı)

## 🐛 Sorun Giderme

### Tarama Durmuş
```powershell
# IIS app pool kontrolü
Get-WebAppPoolState -Name CertMonitor

# Yeniden başlat
Restart-WebAppPool -Name CertMonitor
```

### E-posta Gelmiyor
1. Settings → Test E-postası Gönder
2. Event Viewer → Application logs kontrol et
3. SMTP credentials doğru mu kontrol et
4. Gmail ise: [App Password](https://myaccount.google.com/apppasswords) oluştur

### ChainError Görünüyor
İç CA sertifikaları için:
```powershell
# CA root sertifikasını Trusted Root'a ekle
certutil -addstore Root "ca-cert.cer"
```

## 🔄 Güncelleme

```powershell
# Yedek al
Copy-Item C:\inetpub\CertMonitor\App_Data C:\Backup\App_Data_$(Get-Date -Format 'yyyyMMdd') -Recurse

# Yeni versiyonu yayınla
dotnet publish -c Release -o C:\inetpub\CertMonitor

# IIS yeniden başlat
iisreset
```

## 📝 Lisans

MIT License - Ticari ve kişisel kullanım için ücretsizdir.

## 👤 Geliştirici

**Önder AKÖZ**
- LinkedIn: [linkedin.com/in/onderakoz](https://www.linkedin.com/in/onderakoz)
- GitHub: [github.com/onder7](https://github.com/onder7)

## 🤝 Katkıda Bulunma

1. Fork edin
2. Feature branch oluşturun (`git checkout -b feature/amazing-feature`)
3. Commit edin (`git commit -m 'Add amazing feature'`)
4. Push edin (`git push origin feature/amazing-feature`)
5. Pull Request açın

## ⭐ Yıldız Vermeyi Unutmayın!

Projeyi beğendiyseniz ⭐ vermeyi unutmayın!

---

**Not:** İlk çalıştırmada `appsettings.json` içindeki admin parolasını mutlaka değiştirin!
