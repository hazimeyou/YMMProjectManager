using System.Windows;
using System.Runtime.CompilerServices;

// テストプロジェクトが internal のプローブ補助を参照できるようにしつつ、公開 API は増やさない。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            // テーマ固有の ResourceDictionary の配置先
                                                // (ページまたはアプリケーションの ResourceDictionary に見つからない場合に使われる)
    ResourceDictionaryLocation.SourceAssembly   // 汎用 ResourceDictionary の配置先
                                                // (ページ、アプリ、または任意のテーマ固有 ResourceDictionary に見つからない場合に使われる)
)]

[assembly: InternalsVisibleTo("YMMProjectManager.Tests")]
