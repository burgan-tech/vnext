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

/// <summary>
/// Well-known display values for <see cref="ViewDisplay"/>, grouped by client interface mode.
/// Used as values for <see cref="View.DisplayModes"/> to indicate how the client should present
/// the view. Like <see cref="ViewRenderer"/>, these are a documented vocabulary rather than an
/// enforced enum — the runtime accepts any non-blank value.
/// </summary>
public static class ViewDisplayMode
{
    /// <summary>
    /// Display values for SDI (single-document interface) clients. This is the vocabulary the
    /// legacy string-form <c>display</c> declaration uses.
    /// </summary>
    public static class Sdi
    {
        /// <summary>
        /// Full page display
        /// </summary>
        public const string FullPage = "full-page";

        /// <summary>
        /// Popup/modal display
        /// </summary>
        public const string Popup = "popup";

        /// <summary>
        /// Bottom sheet display
        /// </summary>
        public const string BottomSheet = "bottom-sheet";

        /// <summary>
        /// Top sheet display
        /// </summary>
        public const string TopSheet = "top-sheet";

        /// <summary>
        /// Drawer/side menu display
        /// </summary>
        public const string Drawer = "drawer";

        /// <summary>
        /// Inline display within the page
        /// </summary>
        public const string Inline = "inline";
    }

    /// <summary>
    /// Display values for MDI (multi-document interface) clients, where several documents are open
    /// side by side and the view has to declare where it lands.
    /// </summary>
    public static class Mdi
    {
        /// <summary>
        /// Full page display
        /// </summary>
        public const string FullPage = "full-page";

        /// <summary>
        /// Popup/modal display
        /// </summary>
        public const string Popup = "popup";

        /// <summary>
        /// Bottom sheet display
        /// </summary>
        public const string BottomSheet = "bottom-sheet";

        /// <summary>
        /// Top sheet display
        /// </summary>
        public const string TopSheet = "top-sheet";

        /// <summary>
        /// Drawer/side menu display
        /// </summary>
        public const string Drawer = "drawer";

        /// <summary>
        /// Inline display within the page
        /// </summary>
        public const string Inline = "inline";
    }
}