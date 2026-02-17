# Breaking Change: Function–Workflow Doğrulaması

## Özet

**Instance (`"scope": "I"`)** veya **Flow (`"scope": "F"`)** kapsamındaki bir **fonksiyon** çağrıldığında, ilgili **akış (flow) tanımında** bu fonksiyon `functions` dizisinde tanımlı olmalıdır. Tanımlı değilse API, fonksiyonu çalıştırmak yerine validasyon hatası döner.

## Hata Yanıtı

Fonksiyon, workflow için tanımlı değilse:

- **Mesaj:** `"Function '{functionKey}' is not defined for workflow '{workflowKey}'"`
- **HTTP durumu:** 400 (Validation)
- **Hata kodu:** `Function:800001` (FunctionNotInWorkflow)

## Gerekli Yapılandırma

**Workflow (flow) tanımında** fonksiyon, `functions` listesinde referans verilmiş olmalıdır. Kapsamı `I` veya `F` olan fonksiyonlar yalnızca bu listede yer alıyorsa ilgili workflow bağlamında çalıştırılabilir.

**Örnek (workflow/flow tanımı):**

```json
{
  "key": "parent-flow",
  "domain": "local-test",
  "version": "1.0.0",
  "functions": [
    {
      "key": "function-get-instance-summary",
      "domain": "local-test",
      "flow": "sys-functions",
      "version": "1.0.0"
    }
  ]
}
```

`function-get-instance-summary` fonksiyonu `"scope": "I"` veya `"scope": "F"` ise ve akışın `functions` dizisinde **yoksa**, bu workflow için yapılan ilgili fonksiyon çağrıları yukarıdaki hata ile başarısız olur.

## Kapsam Davranışı

- **Sistem / global** (ör. `sys-functions` flow’undan) ve `I`/`F` kapsamında olmayan fonksiyonlar bu kontrole tabi değildir.
- **Instance (`I`)** ve **Flow (`F`)** kapsamı için fonksiyonun workflow’un `functions` dizisinde tanımlı olması **zorunludur**.
