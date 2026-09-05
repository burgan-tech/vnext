# Senkron zincirde context tekrar kullanımı

## Kapsam

TransitionPipeline içindeki kesintisiz otomatik zincir, ilk girişte instance ve workflow
yükler. Sonraki adımlar aynı UoW içindeki instance ve workflow ile CreateFromPreloaded
üzerinden yeni TransitionExecutionContext oluşturur. Her adımda state/transition çözümü
ve policy kontrolü yeniden yapılır; directives, items ve geçici cache paylaşılmaz.

Runner'ın yeni scope girişleri, subflow dönüşü ve retry yolları bu optimizasyona dahil
değildir. Tracked instance başka DbContext'e taşınmaz. Tamamlanmış instance kontrolü
ve cancellation kontrolü, zincirin sonraki adımı kurulmadan önce korunur.

## Tekrarlanabilir ölçüm

TransitionContextFactoryTests.InlineChain_ShouldReduceRepositoryLoadsAndKeepHopContextsIsolated
aynı gerçek factory ile önce eski yolu (her adımda CreateAsync), ardından yeni yolu
(ilk adım CreateAsync, kalan adımlar CreateFromPreloaded) çalıştırır.

| Zincir | Eski repository yüklemesi | Yeni repository yüklemesi | Eski workflow çözümleme | Yeni workflow çözümleme |
|---|---:|---:|---:|---:|
| 5 adım | 5 | 1 | 5 | 1 |
| 10 adım | 10 | 1 | 10 | 1 |

Bu sayılar mock repository/cache sınırında doğrulanan çağrı sayılarıdır; gerçek SQL
komut sayısı veya uçtan uca süre ölçümü değildir. Workflow zaten ResolvedWorkflow ile
taşınmışsa ilk workflow çözümleme çağrısı da gerekmez. Bir aggregate yüklemesi birden
fazla SQL içerebilir; diğer okuma ve yazımlar bu tablonun dışındadır.

Test ayrıca değişmiş state ve stage'in sonraki context'e yansımasını, geçici items ve
directives'ın sızmamasını ve yeni girişte instance'ın tekrar yüklenmesini doğrular.
InstanceDataWriteService.PersistAsync, yazdığı data satırını AcceptPersistedData ile
instance'a aktarır; mevcut data yazımının lock/head/version okumaları korunmuştur.

## Çalışma zamanı ölçümü

Context için özel metrik sayaçları kullanılmaz; gözlem trace span'ları üzerinden yapılır.

Transition.LoadContext span'ı vnext.context.source etiketiyle iki yolu ayırır. Span
süreleri context hazırlığını gösterir; toplam zincir süresi enclosing request/job
span'ından, SQL sayısı mevcut database span'larından ayrıca ölçülmelidir.

Dağıtım sonrası aynı veri ve tanımlarla 5/10 adımlı zincir, veri yazan zincir ve subflow
giriş/dönüş senaryolarında SQL SELECT/INSERT/UPDATE sayıları ile uçtan uca p50/p95
karşılaştırılmalıdır. Bu değişiklik kapsamında çalışan Docker servisleri yeniden
dağıtılmadı; gerçek PostgreSQL ve uçtan uca gecikme kazanımı henüz ölçülmedi.

## Doğrulama komutu

```sh
dotnet test test/BBT.Workflow.Application.Tests --no-restore --filter 'FullyQualifiedName~TransitionPipelineTests|FullyQualifiedName~TransitionContextFactoryTests|FullyQualifiedName~PostCommitTransitionCoordinatorTests|FullyQualifiedName~TransitionRunnerPostCommitTests'
```
