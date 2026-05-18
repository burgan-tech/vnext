namespace BBT.Workflow.Definitions;

/// <summary>
/// View types
/// </summary>
public enum ViewType
{
    /// <summary>
    /// Json
    /// </summary>
    Json = 1,

    /// <summary>
    /// Html
    /// </summary>
    Html = 2,

    /// <summary>
    /// Markdown
    /// </summary>
    Markdown = 3,

    /// <summary>
    /// Deep link URL for navigation
    /// </summary>
    DeepLink = 4,
    
    /// <summary>
    /// Http
    /// </summary>
    Http = 5,
    
    /// <summary>
    /// Urn
    /// </summary>
    URN = 6
}

/// <summary>
/// Well-known renderer identifiers for UI SDK dispatch.
/// Used as values for <see cref="View.Renderer"/> to indicate which render engine
/// should interpret the view content on the client side.
/// </summary>
public static class ViewRenderer
{
    /// <summary>
    /// Pseudo UI renderer (schema-driven form generation)
    /// </summary>
    public const string PseudoUi = "pseudo-ui";

    /// <summary>
    /// Flutter renderer
    /// </summary>
    public const string Flutter = "flutter";

    /// <summary>
    /// Angular renderer
    /// </summary>
    public const string Angular = "angular";

    /// <summary>
    /// Vue.js renderer
    /// </summary>
    public const string Vue = "vue";

    /// <summary>
    /// React renderer
    /// </summary>
    public const string React = "react";

    /// <summary>
    /// React Native renderer
    /// </summary>
    public const string ReactNative = "react-native";

    /// <summary>
    /// Native iOS renderer
    /// </summary>
    public const string NativeIos = "native-ios";

    /// <summary>
    /// Native Android renderer
    /// </summary>
    public const string NativeAndroid = "native-android";
}