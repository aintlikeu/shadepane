namespace Shadepane.Core.Extensions;

/// <summary>Манифест отсутствует, повреждён или имеет недопустимую структуру.</summary>
public sealed class ManifestException(string message) : Exception(message);

/// <summary>Папка расширения не может быть принята (ссылки, лимиты, недопустимые имена).</summary>
public sealed class ExtensionDirectoryException(string message) : Exception(message);
