// Services/CacheUpdateService.cs
// ⚠️ 이 파일은 제거 예정입니다.
//
// 이전: DbConnectionFactory 기반 ON CONFLICT upsert 방식
// 현재: DbManager.RebuildStockCache() 로 통합 완료
//          호출은 DbManager.Instance.EnsureStockCache() 또는
//                    DbManager.Instance.RebuildStockCache() 사용
//
// 삭제 시 이 파일만 제거하면 됩니다.
// DbConnectionFactory 의존 코드가 없다면 DbConnectionFactory.cs도 함께 제거 검토하세요.

namespace Quant.Core.Services;

// [DELETED] CacheUpdateService 제거됨 — DbManager.RebuildStockCache()로 통합
