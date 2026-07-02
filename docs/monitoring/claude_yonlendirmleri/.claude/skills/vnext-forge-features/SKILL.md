# vnext-forge — Product Overview

## vnext-forge Nedir?

**vnext-forge**, vnext workflow engine ekosistemi icin gelistirilmis bir **workflow tasarim ve yonetim araci**dir. VS Code extension olarak paketlenir (`burgan-tech.vnext-forge`) ve bagimsiz bir urun olarak dagitilir.

Kullanicilara JSON tabanli workflow tanimlari olusturma, gorsel canvas uzerinde is akisi tasarimi, Monaco tabanli kod duzenleme, gercek zamanli dogrulama, runtime baglantisi, simulasyon ve proje iskelesi olusturma gibi uctan uca yetenekler sunar.

## Kimi Hedefler?

- **Workflow gelistiricileri**: vnext engine uzerinde is akislari tasarlayan ve yoneten yazilimcilar
- **Is analistleri**: VS Code ortaminda gorsel workflow tasarimi yapan kullanicilar
- **Engine entegratorleri**: vnext runtime'a workflow yayinlayan, test eden ve deploy eden takim uyeleri

## Ne Amaclar?

1. **Tek ortamda uctan uca workflow yonetimi**: Proje olusturmadan deploy'a kadar tum sureci VS Code icinde kapsar
2. **Gorsel + kod tabanli hibrit tasarim**: React Flow canvas ile gorsel, Monaco ile JSON/C# kod duzenlemeyi birlesitirir
3. **Transport-agnostik mimari**: Ayni React UI hem web tarayicida (HTTP REST) hem VS Code webview'da (postMessage) calisir
4. **Gercek zamanli geri bildirim**: Anlik schema dogrulama, workflow validasyonu ve runtime saglik izleme

## Ne Degildir?

- **Bir runtime degildir**: Workflow'larin yurutulmesi vnext runtime engine'e aittir; vnext-forge yalnizca tasarim ve yonetim katmanidir
- **Bir SaaS platformu degildir**: Tek gelistirici is istasyonuna yonelik yerel aractir
- **Bir kutuphane degildir**: Baska uygulamalara gomulmek icin tasarlanmamistir; bagimsiz urun olarak dagitilir
- **Genel amacli bir IDE degildir**: Yalnizca vnext engine ekosistemi icin ozellestirilmis workflow tasarim aracidir
- **No-code-only bir arac degildir**: Gorsel canvas sunsa da, JSON/C# duzenleme ve gelistirici odakli akislar birincil kullanim modelidir

---

## Feature Detaylari

### 1. Proje Yasam Dongusu Yonetimi

**Proje Olusturma**: `vnextForge.createProject` komutu veya sidebar "Create Project" gorunumu ile yeni vnext projesi olusturulur. `@burgan-tech/vnext-template` sablonundan iskele kurulur; `vnext.config.json`, klasor yapisi (Workflows, Tasks, Schemas, Views, Functions, Extensions) ve baslangic dosyalari otomatik uretilir.

**Proje Iceri Aktarma (Import)**: Mevcut bir vnext projesini (`vnext.config.json` iceren klasor) workspace'e ekler. Workspace detector `vnext.config.json` dosyasini algiladiginda projeyi otomatik olarak kayit defterine (project registry) ekler.

**Proje Listeleme ve Gezinme**: Ana sayfa (landing page) tum projeleri listeler. Her proje karti uzerinden projeye giris yapilir. Proje icinde VS Code benzeri bir kabuk acilir: aktivite cubugu, katlanabilir sidebar, ana duzenleyici alani ve durum cubugu.

**Dosya Agaci (Explorer)**: Sidebar icinde agac gorunumunde proje dosyalari listelenir. Dosya tiklayinca ilgili bilesen editorune veya kod editorune yonlendirilir. Dosya agaci, workspace dosya sistemi degisiklik olaylarini (FS event bus) dinleyerek disk mutasyonlarindan sonra otomatik guncellenir (debounce ~150ms).

**Coklu Sekme Duzenleyici**: Acilan her bilesen veya dosya icin bir editor sekmesi olusturulur. Sekmeler arasi gecis, sekme kapatma, route ile sekme senkronizasyonu saglanir. Bilesen turu (workflow, task, schema vb.) icin ozel sekme ikonu ve basligi goruntulenir.

### 2. Workspace Yapilandirmasi (`vnext.config.json`)

**Yapilandirma Sihirbazi**: `CreateVnextConfigDialog` ile interaktif sihirbaz sunar. Domain, yollar (paths), disa aktarimlar (exports), bagimliliklar (dependencies), referans cozumleme kurallari ve dogrulama ayarlari adim adim yapilandirilir.

**Canli Onizleme**: Sihirbaz icinde duzenlenen yollar (paths) degistiginde, `previewPaths` parametresi uzerinden sunucu tarafinda canli bilesen taramasi calistirilir ve sonuclar anlik olarak gosterilir — kullanici kaydetmeden once hangi bilesenlerin bulunacagini gorur.

**Config Durum Izleme**: `projects/getConfigStatus` metodu ile `vnext.config.json`'un gecerliligi ve eksik alanlari sorgulanir. Gecersiz veya eksik konfigurasyonda kullaniciya uyari gosterilir.

**Bilesen Klasor Yapisi Iskelesi**: `projects/seedVnextComponentLayout` ile `vnext.config.json`'daki `paths` tanimina uygun klasor yapisi (Workflows/, Tasks/, Schemas/ vb.) otomatik olusturulur.

### 3. Gorsel Workflow Tasarimi (Canvas)

**React Flow Tabanli Canvas**: Surukle-birak ile state ve transition dugumleri olusturulur. Canvas uzerinde zoom, pan, secim ve coklu secim destegi vardir.

**Dugum Turleri**:
- **Initial State** (`stateType: 1`): Workflow'un baslangic noktasi — her workflow'da yalnizca bir tane
- **Intermediate State** (`stateType: 2`): Is akisindaki ara adimlar
- **Final State** (`stateType: 3`): Sonlandirma durumlari — birden fazla olabilir
- **SubFlow State** (`stateType: 4`): Baska bir workflow'u alt is akisi olarak cagirir
- **Wizard State** (`stateType: 5`): Tek transition alan sihirbaz adimlari
- **Error Boundary**: Hata yakalama sinirlari

**Kenar (Edge) Turleri**: Transition'lar kenarlarla temsil edilir. Manual (`triggerType: 0`), Automatic (`triggerType: 1`), Scheduled (`triggerType: 2`) ve Event (`triggerType: 3`) tetikleme turleri gorsel olarak ayirt edilir.

**Ozellik Panelleri**: Bir state veya transition secildiginde, sag tarafta ozellik paneli acilir. State icin: ad, tur, etiketler, onEntries/onExits task listesi, view baglantisi. Transition icin: hedef state, tetikleme turu, schema baglantisi, rule referansi, timer ayarlari.

**Workflow Metadata Paneli**: Workflow'un genel ozelliklerini (key, version, domain, tags, labels, type, startTransition) duzenlemek icin ayri bir panel.

**Otomatik Yerlesim (Auto-Layout)**: Canvas uzerindeki dugumleri otomatik olarak duzenli bir sekilde konumlandirir.

**Canvas Kaliciligi**: Dugum pozisyonlari ve canvas gorunumu (zoom seviyesi, pan pozisyonu) kaydedilir ve tekrar acildiginda geri yuklenir.

### 4. Bilesen Editorleri

Her vnext bilesen turu icin ozel form tabanli editor sunar. Tum editorler ayni kayit/yayinlama altyapisini paylaşir.

**Workflow Editor (`FlowEditorView`)**: Ikiye bolunmus gorsel editor — sol tarafta canvas, sag tarafta ozellik paneli veya script editor. Canvas ve JSON/CSX arasinda senkronize duzenleme. Yeniden boyutlandirilabilir (resizable) paneller.

**Task Editor**: Task turune gore dinamik form alanlari:
- **Script Task (type: 7)**: Yapilandirma (config) alani
- **HTTP Task (type: 6)**: URL, HTTP metodu, header, govde yapilandirmasi
- **Human Task (type: 5)**, **Condition (type: 8)**, **Timer (type: 9)**, **Notification (type: 10)** ve diger turler

**Schema Editor**: JSON Schema Draft 2020-12 tanimlari icin form + JSON editor. Schema turu (workflow, task, function, view, schema, extension, headers) secimi. `$schema`, `$id`, `required` alanlari ve ozellik tanimlari.

**View Editor**: Mobil uygulama gorunumu tanimlari. Content turu (JSON/HTML/Markdown), gorunum modu (`full-page`, `popup`, `bottom-sheet`, `top-sheet`, `drawer`, `inline`), platform bazli override'lar (android, ios, web), cok dilli etiketler.

**Function Editor**: Fonksiyon bilesen tanimlari icin form tabanli editor.

**Extension Editor**: Extension bilesen tanimlari icin form tabanli editor.

**Bilesen Olusturma Komutlari**: Her bilesen turu icin ayri olusturma komutu: `forgeCreateWorkflow`, `forgeCreateTask`, `forgeCreateSchema`, `forgeCreateView`, `forgeCreateFunction`, `forgeCreateExtension`. Explorer baglam menusunde ilgili klasore sag tiklandiginda uygun olusturma komutu gosterilir.

### 5. Kod Editoru (Monaco)

**Genel Amacli Dosya Duzenleme**: Proje icindeki herhangi bir dosya (`.json`, `.csx`, `.http` vb.) Monaco editorde acilir. Kaydet (Ctrl+S), geri al (undo) ve LSP entegrasyonu saglanir.

**CSX Script Duzenleme**: C# Script dosyalari (`.csx`) icin tam IntelliSense destegi. Mapping (`IMapping`) ve rule (`IConditionMapping`) sablonlari icin VS Code snippet'lari (`csx.code-snippets`).

**JSON Bilesen Duzenleme**: Workflow, task, schema ve diger bilesen JSON dosyalari Monaco'da acildiginda, `vnextForge.componentEditor` custom editor devreye girer ve designer gorunumunde acar. Kullanici isterse "Open with Text Editor" ile ham JSON'a donebilir.

**Script Panel (Flow Editor Icinde)**: Workflow editor icinde ayrilmis bir script paneli. Bir state'in onEntries/onExits mapping'ini sectiginde, ilgili `.csx` dosyasi script panelinde acilir ve yerinde duzenlenebilir.

### 6. C# Dil Destegi (LSP)

**OmniSharp / csharp-ls Entegrasyonu**: Extension aktivasyonunda, LSP sunucusu otomatik olarak indirilir ve kurulur (`autoInstall` ayari). Tek bir LSP stack instance'i (`createExtensionHostLspStack`) tum oturumlar icin paylasilir.

**Designer Webview LSP**: Monaco editordeki `.csx` dosyalari icin LSP JSON-RPC mesajlari `postMessage` uzerinden tunellenir. Her webview oturumu icin bagimsiz LSP session yonetimi (`sessionId` bazli connect/message/disconnect).

**Yerel VS Code Editor LSP**: `.csx` dosyalari VS Code'un yerel text editorunde acildiginda da IntelliSense calisir. `vscode-languageclient` ile `**/*.csx` pattern'ine eslesen dosyalar icin dil istemcisi kayit edilir.

**Yetenekler**: Kod tamamlama (IntelliSense), tanimlama'ya git, hover bilgisi, hata/uyari isaretcileri (diagnostics), referans bulma.

### 7. Dogrulama Sistemi (Validation)

**Workflow Graf Dogrulamasi**: State turleri (tek initial, en az bir final), transition kurallari (auto transition'da rule zorunlulugu, wizard state'te maks. 1 transition), referans butunlugu (var olmayan task/schema referanslari) kontrol edilir.

**Bilesen Schema Dogrulamasi**: Her bilesen JSON dosyasi `@burgan-tech/vnext-schema` paketindeki sema tanimlarina karsi dogrulanir. Gecersiz alanlar, eksik zorunlu degerler ve tip uyumsuzluklari raporlanir.

**Gercek Zamanli Monaco Isaretcileri**: Dogrulama sonuclari Monaco editorde satiraltı hata/uyari isaretcileri (markers) olarak goruntulenir. Duzenleme sirasinda anlik geri bildirim saglanir.

**Dogrulama Paneli**: Tum dogrulama hatalari ve uyarilari toplu bir listede gosterilir. Bir hataya tiklandiginda ilgili satira/alana yonlendirilir.

**Workflow Validation Sync**: Canvas uzerinde yapilan degisiklikler anlik olarak dogrulama motoruna iletilir ve sonuclar hem canvas (dugum uzerinde hata ikonu) hem editor (markers) uzerinde yansitilir.

### 8. Runtime Baglantisi ve Saglik Izleme

**Yapilandirilabilir Runtime URL**: `vnextForge.vnextRuntimeUrl` ayari ile hedef runtime adresi belirlenir (varsayilan `http://localhost:4201`). Ek URL'ler `runtimeAllowedBaseUrls` ile izin listesine eklenebilir.

**Runtime Saglik Izleme (Health Check)**: `health/check` metodu periyodik olarak runtime'in `/health` endpoint'ini sorgular. Baglanti hatasi durumunda UI'da `status: down` gosterilir — hata firlatmaz, graceful degrade eder. `RuntimeHealthSync` bileseni kabuk genelinde saglik durumunu senkronize tutar.

**Arka Plan Revalidasyonu**: `runtimeRevalidationMinIntervalSeconds` ayari (varsayilan 30sn) ile arka planda periyodik runtime durumu yenilenir. Designer acikken runtime'in erisilebiligini surekli izler.

**Runtime Proxy**: Webview'dan runtime'a yapilan tum HTTP istekleri extension host uzerinden proxy edilir. URL allowlist ile SSRF savunmasi saglanir; `allowRuntimeUrlOverride` kapali iken istek bazinda URL degisikligi engellenir.

### 9. Quick Run

**IDE Icinden Workflow Calistirma**: Quick Run paneli ile bir workflow secilir ve dogrudan IDE icinden baslatilir. Yeni instance olusturma, transition tetikleme, durum sorgulama — hepsi tek panelden.

**Desteklenen Islemler**: Instance baslat, transition tetikle (data ile), state/view/data/schema/history sorgula, basarisiz instance'i yeniden dene, aktif instance'lari listele.

**Polling Yapilandirmasi**: `quickRun.pollingRetryCount` (varsayilan 6) ve `quickRun.pollingIntervalMs` (varsayilan 10ms) ayarlari ile async islemlerin tamamlanmasini bekleme davranisi kontrol edilir.

**Erisim Yollari**: Sidebar "Quick Run" gorunumu, komut paleti (`vnextForge.openQuickRun`), workflow dosyasina sag tikla (`vnextForge.openQuickRunFromFile`), editor baslik menusunden.

### 10. Ortam ve Deploy Yonetimi

**Coklu Ortam (Environment Management)**: Sidebar "Environments" gorunumunde ortamlar tanimlanir, duzenlenir ve silinir. `setActiveEnvironment` ile aktif ortam secilir; `switchEnvironment` ile hizli gecis yapilir. Her ortam icin saglik kontrolu (`checkHealth`) calistirilabilir.

**wf CLI Entegrasyonu**:
- `wf update --all`: Tum bilesen tanimlarini aktif ortama deploy et
- `wf update`: Yalnizca degisen bileseleri deploy et
- `wf csx --all`: Tum CSX dosyalarini guncelle
- `installWfCli`: Workflow CLI aracini kur

**Paket Deploy Paneli**: Sidebar "Package Deploy" gorunumunde deploy islemleri yonetilir.

**Proje Derleme**: `validateProject` (tum bilesenleri dogrula), `buildRuntime` (runtime paketi olustur), `buildReference` (referans paketi olustur), `generateDocs` (dokumantasyon uret) komutlari sidebar "Project" gorunumunden erisilebildir.

### 11. VS Code Derinlemesine Entegrasyon

**Ozel Aktivite Cubugu**: "vNext Forge Tools" ikonu ile VS Code aktivite cubugundan erisilen ozel sidebar. 6 gorunu: Settings, Project, Create Project, Environments, Package Deploy, Quick Run. `vnextForge.isVnextWorkspace` context key'ine gore gorunum durumu degisir.

**Custom Editor Provider**: `.json` dosyalari `vnextForge.componentEditor` ile acilir. Bilesen dosyalari (workflow, task, schema vb.) otomatik olarak designer gorunumunde gosterilir; bilesen olmayan JSON dosyalari yerel text editore yonlendirilir.

**Baglam Menusu Entegrasyonu**: Explorer'da ve editor basliginda vnext bilesen dosyalarina sag tiklandiginda "Open with vNext Forge", "Open with Text Editor" ve (workflow dosyalari icin) "Quick Run" secenekleri gosterilir. Bilesen klasorlerine sag tiklandiginda ilgili turde yeni bilesen olusturma secenegi gosterilir.

**Otomatik Workspace Algilama**: `workspaceContains:vnext.config.json` activation event'i ile proje acildiginda extension otomatik aktive olur. `VnextWorkspaceDetector` dosya degisikliklerini izler, yeni eklenen veya kaldirilan vnext projelerini otomatik algilar.

**Material Icon Theme**: `applyMaterialIconAssociations` komutu ile vnext bilesen dosya ve klasorleri icin ozel ikonlar VS Code'un Material Icon Theme yapilandirmasina eklenir. `removeMaterialIconAssociations` ile geri alinir.

**Host → Webview Navigasyon**: `vnextForge.openDesigner` komutu veya explorer'dan dosya acildiginda, extension host webview'a `navigate` mesaji gonderir. Webview hazir degilse mesaj kuyruge alinir ve `webview-ready` sinyalinden sonra iletilir.

### 12. vnext Bilesen Kesfetme (BFF Discovery)

**Sunucu Tarafli Tarama**: Proje icerisindeki tum vnext bilesen dosyalari (`vnext.config.json`'daki `paths` tanimina gore) sunucu/extension host tarafinda tek seferde taranir. Istemci dosya agacini gezip tek tek dosya okumaz.

**Kategori Bazli Listeleme**: Her bilesen turu icin ayri RPC metodu (`vnext/tasks/list`, `vnext/workflows/list`, `vnext/schemas/list` vb.) veya tumu icin tek filtreli metod (`vnext/components/list` + opsiyonel `category` parametresi).

**Bilesen Secici Dialoglari**: Canvas uzerinde bir task referansi eklerken `ChooseExistingTaskDialog`, baska bilesen turleri icin `ChooseExistingVnextComponentDialog` acilir. Mevcut bilesenler listelenir ve secim yapilir — elle key/version/domain/flow yazmaya gerek kalmaz.

### 13. Dosya ve Workspace Islemleri

Dosya sistemi islemleri (read, write, delete, mkdir, rename, browse, search) transport-agnostik RPC metotlari uzerinden gerceklestirilir.

**FS Event Bus**: Dosya yazma, silme, yeniden adlandirma gibi mutasyonlar basarili oldugunda otomatik olay yayinlanir. Web shell'de sidebar dosya agaci bu olaylari dinleyerek kendini gunceller (debounce ~150ms ile toplu yenileme).

### 14. Hata Yonetimi ve Bildirimler

**Yapılandırılmis Hata Taksonomisi**: Tum katmanlar `VnextForgeError` kullanir. Her hata `code`, `context.source`, `context.layer` ve `traceId` bilgisi tasir. `toUserMessage()` ile kullanici dostu mesaj, `toLogEntry()` ile log detayi uretilir.

**Bildirim Portu**: Host-agnostik `showNotification(...)` API'si — web'de Sonner toast, extension'da VS Code native bildirim.
