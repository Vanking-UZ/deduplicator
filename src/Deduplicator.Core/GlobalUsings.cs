// Снимаем неоднозначность: System.IO.MatchType приходит из ImplicitUsings и конфликтует
// с доменным перечислением. Алиас закрепляет нашу версию во всём проекте.
global using MatchType = Deduplicator.Core.Models.MatchType;
