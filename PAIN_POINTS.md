# vNext Platform: Teknik Borç ve Mimari Değerlendirme

Bu belge, vNext Platform deposundaki tespit edilen "pain point"leri, teknik borçları ve iyileştirme önerilerini detaylandırmaktadır.

## 1. Geliştirici Deneyimi (DX)

### .NET 10 & PostSharp Kurulum Zorlukları
*   **Targeting Pack Bağımlılığı**: Yeni geliştiricilerin PostSharp uyumluluğu için manuel bir kurulum betiği (`setup-netstandard-ref.sh`) çalıştırması gerekmektedir. Bu, standart .NET geliştirme akışının dışındadır ve otomatize edilmemiş ortamlarda (CI/CD, yeni bilgisayar kurulumu) hata kaynağıdır.
*   **PostSharp Derleme Yükü**: AOP (Aspect-Oriented Programming) için PostSharp kullanımı derleme sürelerini uzatmakta ve IDE tarafında özel yapılandırmalar gerektirmektedir.

### Yerel Geliştirme Karmaşıklığı
*   **Ağır Altyapı Bağımlılığı**: Sistemin yerel çalışması PostgreSQL, Redis, Dapr ve Jaeger gibi birçok bileşene Docker üzerinden ihtiyaç duyar. Bu durum, basit bir kod değişikliğini test etmeyi bile zorlaştırmaktadır.
*   **Dapr Bağımlılığı**: Servisler arası iletişimdeki sıkı Dapr bağımlılığı, sidecar olmadan servislerin izole şekilde hata ayıklanmasını (debug) zorlaştırır.

### Proje Sayısı (Granularity)
*   **Aşırı Bölünmüş Yapı**: Çözümde 20'den fazla proje bulunmaktadır. Clean Architecture desteklense de, bu durum navigasyon zorluğu, yavaş IDE performansı ve uzun derleme sürelerine neden olmaktadır.

## 2. Güvenlik ve Güvenilirlik

### SQL Injection Riskleri (EF1002)
*   **Raw SQL Kullanımı**: `EfCoreInstanceRepository.cs` ve `MultiSchemaMigrator.cs` içerisinde şema adları, tablo adları ve ORDER BY cümleleri için `FromSqlRaw` ve string interpolation kullanılmaktadır.
*   **Validator Bağımlılığı**: `ISchemaValidator` bu riskleri azaltsa da, desenin kendisi güvenlik uyarılarını (EF1002) tetiklemekte ve denetimlerde sürekli risk teşkil etmektedir. Dinamik tablo/şema seçimi için daha yapısal bir yaklaşım (örneğin Interceptor'lar üzerinden) daha güvenli olacaktır.

### Zaman Bağımlı Mantık Testleri
*   **Doğrudan `DateTime.UtcNow` Kullanımı**: Kod içerisinde bir saat soyutlaması (`ISystemClock` veya `TimeProvider`) yerine doğrudan `DateTime.UtcNow` kullanılmaktadır. Bu durum, zaman aşımı (timeout) veya ETag geçerliliği gibi özelliklerin test edilmesini zorlaştırmakta ve testlerde `Thread.Sleep` kullanımına yol açmaktadır.

### AmbientServiceProvider Kullanımı (Service Location Anti-Pattern)
*   **Gizli Bağımlılıklar**: Kodun birçok yerinde `AmbientServiceProvider.Current` üzerinden servis çözümlenmektedir. Bu, bağımlılıkların constructor üzerinden açıkça görülmesini engeller, birim test (unit test) yazmayı zorlaştırır ve çalışma zamanında (runtime) "null reference" hatalarına davetiye çıkarır.

## 3. Mimari ve Sürdürülebilirlik

### Multi-Schema Ölçeklenebilirlik Sorunları
*   **Şema Yönetimi**: Her iş akışı (flow) için ayrı bir PostgreSQL şeması oluşturulmaktadır. Binlerce akış olan bir sistemde bu durum; veritabanı migration sürelerinin uzamasına, connection pool sorunlarına ve cross-flow analizlerin zorlaşmasına neden olabilir.

### Event Handling "Dual-Processing" Karmaşası
*   **Boilerplate Yükü**: Her domain event için hem `IEventPublishHook` (senkron) hem de `IEventHandler` (asenkron) yazılması gerekmektedir. Bu durum geliştirme maliyetini artırmakta ve birinin unutulması durumunda veri tutarsızlıklarına yol açabilmektedir.

### HookedDistributedEventBus Fallback Mantığı
*   **Tutarsızlık Riski**: `HookedDistributedEventBus` içerisindeki mantıkta; eğer hook'lar başarılı olursa event publish edilmemekte, başarısız olursa fallback olarak publish edilmektedir. Bu mantık, event'in ulaşıp ulaşmadığı konusunda kafa karışıklığı yaratabilir ve "at-least-once" delivery garantilerini bozabilir.

### TaskExecutionEngine Karmaşıklığı
*   **Aşırı Sorumluluk**: `TaskExecutionEngine`; retry, hata yönetimi (boundary), metrikler ve persistence gibi çok fazla sorumluluğu tek bir sınıfta barındırmaktadır. Bu durum sınıfın bakımını ve test edilmesini zorlaştırmaktadır.

## 4. Kalite Güvencesi (QA)

### Kırılgan Testler
*   **`Thread.Sleep` Kullanımı**: `CacheItemTests.cs` ve `InstanceTaskTests.cs` gibi testlerde asenkron süreçleri beklemek için `Thread.Sleep` kullanılmaktadır. Bu, testleri yavaşlatır ve CI/CD ortamlarında performans dalgalanmaları nedeniyle rastgele başarısızlıklara (flaky tests) sebep olur.

### Entegrasyon Testi Ortamı
*   **Docker Bağımlılığı**: `Testcontainers` kullanımı, Docker olmayan veya kısıtlı Docker erişimi olan ortamlarda testlerin çalışmasını engellemektedir (Örneğin: `DockerApiException: InternalServerError` hataları).

---

## Öneriler (Recommendations)

### 1. Mimari İyileştirmeler
*   **TimeProvider Soyutlaması**: .NET 8+ ile gelen `TimeProvider` soyutlamasına geçilmelidir. Bu sayede testlerde zaman "fake" edilebilir ve `Thread.Sleep` kullanımına gerek kalmaz.
*   **AmbientServiceProvider'dan Kaçınma**: Servisler constructor injection ile alınmalıdır. Scope yönetimi gereken yerlerde `IServiceScopeFactory` açıkça kullanılmalıdır.
*   **Event Handling Sadeleştirme**: Senkron ve asenkron handler'lar arasındaki fark netleştirilmeli, mümkünse Aether SDK seviyesinde bir "outbox" mekanizması ile bu ikili yapı otomatikleştirilmelidir.

### 2. Güvenlik ve Veritabanı
*   **Dinamik Şema Yönetimi**: SQL string birleştirmek yerine, EF Core `IModelCacheKeyFactory` ve Interceptor'lar kullanarak şema değişimi daha güvenli hale getirilmelidir. EF1002 uyarıları sıfıra indirilmelidir.
*   **Schema Per Flow Yerine Row-Level Security (RLS)**: Çok fazla flow olan senaryolarda, binlerce şema yerine PostgreSQL RLS (Row-Level Security) veya `TenantId` bazlı veri ayrımı değerlendirilmelidir.

### 3. Geliştirici Deneyimi (DX)
*   **Kurulumun Otomatize Edilmesi**: PostSharp bağımlılığı ya minimize edilmeli ya da kurulum süreci bir `NuGet` paketi veya `Directory.Build.targets` içerisinde tamamen otomatik hale getirilmelidir.
*   **Dapr-Free Local Mode**: Servislerin Dapr sidecar'ı olmadan (Mock servisler ile) çalışabilmesini sağlayan bir "Standalone" mod eklenmelidir.

### 4. Test Stratejisi
*   **`Thread.Sleep` Yerine `TaskCompletionSource` veya `Poll`**: Testlerde bir durumun oluşmasını beklemek için `Thread.Sleep` yerine polly tabanlı beklemeler veya `TaskCompletionSource` gibi sinyal mekanizmaları kullanılmalıdır.
*   **In-Memory Database Opsiyonu**: Entegrasyon testlerinin bir kısmı (şema gerektirmeyenler) `Testcontainers` yerine SQLite In-Memory veya EF Core In-Memory ile çalıştırılarak hızlandırılmalı ve Docker bağımlılığı azaltılmalıdır.
