# Sunum Büyüteci

Projeksiyon veya TV'ye sunum yaparken ekranın bir bölümünü **yalnız izleyicilere** büyütülmüş gösteren Windows uygulaması.

## Neden?

Bir derste hoca sunum yaparken Windows Büyüteci'ni kullanıyordu. Büyüteç kendi ekranının üzerinde çalıştığı için sürekli tam ekran / büyüt arasında geçiş yapmak zorunda kalıyor, büyütünce de dışarıda kalan kısmı kendisi göremiyordu. Bu proje o sorundan doğdu:

- Siz kendi ekranınızı **normal boyutta** görmeye devam edersiniz.
- Sunum ekranında (projeksiyon/TV) ise seçtiğiniz bölge **büyütülmüş** görünür.
- Kendi ekranınızda yalnızca büyütülen alanı gösteren bir çerçeve çıkar; çerçeve izleyicilere görünmez.

## İndirme

[Releases](../../releases) sayfasından `SunumBuyuteci.exe` dosyasını indirin. Kurulum gerekmez; .NET yüklü olması da gerekmez.

> Not: Windows SmartScreen imzasız uygulama uyarısı verebilir: "Ek bilgi → Yine de çalıştır".

## Kullanım

1. Projeksiyonu bağlayın, `Win + P` → **Uzat**.
2. `SunumBuyuteci.exe`'yi açın, sunum ekranını seçip **Başlat**'a basın.
3. Kontrol penceresinden veya kısayollarla (ör. `Ctrl + Alt + Fare tekerleği`) yakınlaştırın.

Ayrıntılar ve tüm kısayollar: [docs/KULLANIM.md](docs/KULLANIM.md)

## Derleme

.NET 10 SDK gerekir (Windows, WPF).

```
dotnet publish src\SunumBuyuteci\SunumBuyuteci.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

## Lisans

[MIT](LICENSE)
