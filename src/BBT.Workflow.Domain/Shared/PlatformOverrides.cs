using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Domain.Values;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Shared;

    /// <summary>
    /// Platform-specific overrides for view content.
    /// This class is deprecated (Issue #56). Use rule-based view selection in State/Transition view definitions instead.
    /// </summary>
    [Obsolete("PlatformOverrides is deprecated. Use rule-based view selection in State/Transition view definitions instead. See Issue #56.")]
    public class PlatformOverrides
    {
        private PlatformOverrides()
        {
        }
        
        [JsonConstructor]
        internal PlatformOverrides(
           PlatformOverride? android,
           PlatformOverride? ios,
           PlatformOverride? web)
        {
            Android = android;
            Ios = ios;
            Web = web;
        }
        
        public PlatformOverride? Android { get; private set; }
        public PlatformOverride? Ios { get; private set; }
        public PlatformOverride? Web { get; private set; }

    }
    public class PlatformOverride : ValueObject
    {
        private PlatformOverride()
        {
        }

        [JsonConstructor]
        internal PlatformOverride(
            ViewType? type,
            string content,
            string display)
        {
            Content = Check.NotNullOrEmpty(content, nameof(Content));
            Display = Check.NotNullOrEmpty(display, nameof(Display), LanguageLabelConstants.MaxLabelLength);
            Type = type ?? ViewType.Json;
        }

        /// <summary>
        /// The text content to be displayed to the user.
        /// </summary>
        public string Content { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public string Display { get; private set; }
        public ViewType? Type { get; private set; } = ViewType.Json;
        
        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return Content;
            yield return Display;
            yield return Type?.ToString() ?? string.Empty;
        }
    }
    public static class PlatformConst
    {
        public const string Web = "web";
        public const string Ios = "ios";
        public const string Android = "android";
    }
