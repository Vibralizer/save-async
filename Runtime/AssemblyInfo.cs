// MIT License - Copyright (c) 2025 BUCK Design LLC - https://github.com/buck-co

using System.Runtime.CompilerServices;

// The package's EditMode tests use SaveManager's internal test hooks.
[assembly: InternalsVisibleTo("BUCK.SaveAsync.Editor.Tests")]
