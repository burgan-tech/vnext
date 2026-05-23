# Kural Tabanli View Secimi

## Genel Bakis

Kural tabanli view secimi, calisma zamaninda kosullara gore farkli view'lerin dinamik olarak secilmesini saglar. Bu ozellik, is akisi mantigi degistirilmeden platforma ozel arayuzler, role dayali view'ler ve kosullu UI renderlamasi yapilmasina olanak tanir.

## Kullanim Alanlari

- **Platforma ozel view'ler**: iOS, Android ve Web istemcileri icin farkli view'ler gosterme
- **Role dayali view'ler**: Kullanici rollerine gore farkli arayuzler gosterme
- **Kosullu UI**: Instance verilerine veya duruma gore view renderlama
- **A/B Testleri**: Deney kosullarina gore farkli view'ler sunma

## JSON Semasi

`views` ozelligi bir view entry dizisi kabul eder. Her entry, ne zaman secilmesi gerektigini belirleyen opsiyonel bir `rule` icerebilir.

```json
{
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ViewIosRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"ios\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "my-domain",
        "key": "ios-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["ext1", "ext2"]
    },
    {
      "view": {
        "domain": "my-domain",
        "key": "default-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## ViewEntry Ozellikleri

| Ozellik | Tip | Zorunlu | Aciklama |
|---------|-----|---------|----------|
| `rule` | ScriptCode | Hayir | Kosullu degerlendirme icin JavaScript ifadesi. Atlanirsa, varsayilan olarak davranir. |
| `view` | Reference | Evet | Yuklenecek view bilesenine referans. |
| `loadData` | boolean | Hayir | View ile birlikte instance verisinin yuklenip yuklenmeyecegi. Varsayilan: false. |
| `extensions` | string[] | Hayir | Bu view secildiginde calistirilacak extension listesi. |

### Rule (ScriptCode) Ozellikleri

| Ozellik | Tip | Zorunlu | Aciklama |
|---------|-----|---------|----------|
| `location` | string | Hayir | Script konumu. Gomulu kod icin `"inline"` kullanin. |
| `code` | string | Evet | IConditionMapping interface'ini implemente eden C# kodu. |
| `encoding` | string | Hayir | Duz metin icin `"NAT"`, Base64 kodlu icin `"B64"`. |

### View (Reference) Ozellikleri

| Ozellik | Tip | Zorunlu | Aciklama |
|---------|-----|---------|----------|
| `domain` | string | Evet | View'in kayitli oldugu domain. |
| `key` | string | Evet | View'in benzersiz anahtari. |
| `version` | string | Evet | View versiyonu (semver formati). |
| `flow` | string | Evet | Flow tipi, genellikle `"sys-views"`. |

## Kural Degerlendirmesi

### Degerlendirme Sirasi

1. View'ler **dizi sirasina gore** degerlendirilir (ilkten sona)
2. **Ilk eslesen kural** kazanir ve o view dondurulur
3. **Kurali olmayan** bir entry varsayilan/fallback olarak davranir
4. Varsayilan view'i her zaman dizide **en sona** yerlestirin

### Mevcut ScriptContext Ozellikleri

`Handler` metodu icinde `ScriptContext` uzerinden asagidaki ozelliklere erisebilirsiniz:

| Ozellik | Tip | Aciklama |
|---------|-----|----------|
| `context.Headers` | Dictionary | HTTP istek basliklari |
| `context.QueryParameters` | Dictionary | URL sorgu parametreleri |
| `context.Instance.Data` | dynamic | Mevcut instance verisi |
| `context.Instance.Key` | string | Instance anahtari |
| `context.State` | string | Mevcut state anahtari |
| `context.Transition` | string | Mevcut transition anahtari (varsa) |

## Ornekler

### Ornek 1: Platform Tabanli View Secimi

`x-platform` basligina gore farkli view'ler secme:

```json
{
  "key": "checkout-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class CheckoutIosRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"ios\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "ecommerce",
        "key": "checkout-ios",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class CheckoutAndroidRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"android\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "ecommerce",
        "key": "checkout-android",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "ecommerce",
        "key": "checkout-web",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Ornek 2: Role Dayali View Secimi

Instance verisindeki kullanici rolune gore farkli view'ler gosterme:

```json
{
  "key": "approval-state",
  "stateType": 2,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ApprovalAdminRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.userRole == \"admin\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "hr",
        "key": "approval-admin-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["adminActions"]
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ApprovalManagerRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.userRole == \"manager\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "hr",
        "key": "approval-manager-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "hr",
        "key": "approval-default-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Ornek 3: Veri Odakli View Secimi

Instance veri degerlerine gore view secme:

```json
{
  "key": "payment-state",
  "stateType": 2,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class HighValuePaymentRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            decimal amount = context.Instance.Data.amount;\n            if (amount > 10000)\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "finance",
        "key": "high-value-payment-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["riskAssessment", "managerApproval"]
    },
    {
      "view": {
        "domain": "finance",
        "key": "standard-payment-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Ornek 4: Query Parameter Tabanli Secim

URL sorgu parametrelerine gore view secme:

```json
{
  "key": "dashboard-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class DashboardCompactRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.QueryParameters?[\"mode\"] == \"compact\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "portal",
        "key": "dashboard-compact",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class DashboardDetailedRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.QueryParameters?[\"mode\"] == \"detailed\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "portal",
        "key": "dashboard-detailed",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "portal",
        "key": "dashboard-default",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Ornek 5: Birden Fazla Kosul Iceren Karmasik Kural

Tek bir kuralda birden fazla kosulu birlestirme:

```json
{
  "key": "onboarding-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class IosFirstTimeUserRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            bool isIos = context.Headers?[\"x-platform\"] == \"ios\";\n            bool isFirstTime = context.Instance.Data.isFirstTimeUser == true;\n            return isIos && isFirstTime;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "onboarding",
        "key": "ios-first-time-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class FirstTimeUserRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.isFirstTimeUser == true)\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "onboarding",
        "key": "first-time-user-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "onboarding",
        "key": "returning-user-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## Transition View'leri

Ayni `views` dizi formati transition'larda da kullanilabilir:

```json
{
  "key": "submit-transition",
  "target": "review-state",
  "triggerType": 0,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class SubmitMobileRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            string platform = context.Headers?[\"x-platform\"];\n            if (platform == \"ios\" || platform == \"android\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "forms",
        "key": "submit-mobile-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "forms",
        "key": "submit-desktop-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## En Iyi Uygulamalar

1. **Her zaman varsayilan view ekleyin**: Fallback olarak dizinin sonuna `rule` olmayan bir entry yerlestirin.

2. **Kurallari ozelden genele sirayla dizin**: Daha ozel kurallar genel olanlardan once gelmeli.

3. **Kurallari basit tutun**: Karmasik mantik extension'larda veya backend servislerinde ele alinmali.

4. **Anlamli view anahtarlari kullanin**: View'leri aciklayici sekilde adlandirin (ornegin: `checkout-ios`, `approval-admin-view`).

5. **Tum yollari test edin**: Her kural yolunun uygun kosullarla test edildiginden emin olun.

## Hata Yonetimi

- Hicbir kural eslesmezse ve varsayilan view yoksa hata dondurulur
- Basarisiz kural degerlendirmesi loglanir ve sonraki kural degerlendirilir
- Gecersiz kural sozdizimi kuralin basarisiz olmasina neden olur (sonrakine devam eder)

## Ilgili Dokumantasyon

- [Scripting Engine](./scripting-engine.md) - JavaScript ifade sozdizimi
- [Instance Filtering](./instance-filtering.md) - Sorgu parametresi kullanimi
