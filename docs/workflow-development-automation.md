# Workflow Development Automation

This document explains how to use the automated system for updating workflow JSON files from CSX source files.

## 🚀 Features

- **Automatic Base64 Encoding**: Removes `#load` statements from CSX files and encodes them to base64 format
- **Batch Processing**: Updates all workflow directories in bulk
- **File Watching**: Automatically monitors CSX file changes
- **VS Code Integration**: Easy access through tasks and keyboard shortcuts

## 📋 Usage Methods

### 1. Creating New Files

**Create Mapping File (Recommended)**
```
Ctrl + Shift + M  (Windows/Linux)
Cmd + Shift + M   (Mac)
```
Enter filename when prompted (e.g., "MyAccountMapping")

**Create Rule File**
```
Ctrl + Shift + R  (Windows/Linux)
Cmd + Shift + R   (Mac)
```
Enter filename when prompted (e.g., "ValidationRule")

**Using Code Snippets**
1. Create a new .csx file
2. Type `wfmapping` or `wfrule` 
3. Press `Tab` to expand template

### 2. Manual Update (Single Workflow)

**Method 1: Keyboard Shortcut**
```
Ctrl + Shift + W  (Windows/Linux)
Cmd + Shift + W   (Mac)
```

**Method 2: Command Palette**
1. Open command palette with `F1` or `Ctrl + Shift + P` (Windows/Linux) / `Cmd + Shift + P` (Mac)
2. Type `Tasks: Run Task`
3. Select `Update Workflow Rules`

**Method 3: Terminal**
```bash
node .vscode/scripts/update-workflow-rules.js examples/checking-account-opening
```

### 3. Batch Update (All Workflows)

**Method 1: Keyboard Shortcut**
```
Ctrl + Shift + Alt + W  (Windows/Linux)
Cmd + Shift + Alt + W   (Mac)
```

**Method 2: Command Palette**
1. Open command palette with `F1`
2. Type `Tasks: Run Task`
3. Select `Update All Workflow Rules`

**Method 3: Terminal**
```bash
node .vscode/scripts/update-all-workflows.js
```

### 4. Automatic Update (File Watching)

**Using Command Palette:**
1. Open command palette with `F1`
2. Type `Tasks: Run Task`
3. Select `Watch Workflow Changes`

**Using Terminal:**
```bash
node .vscode/scripts/watch-workflows.js
```

## 📁 Supported File Structure

The system looks for the following structure:

```
examples/
├── workflow-name/
│   ├── workflow-definition.json    # Workflow definition file
│   └── src/                       # CSX source files
│       ├── Rule1.csx
│       ├── Rule2.csx
│       └── Mapping1.csx
```

## 🔧 How It Works

1. **CSX File Scanning**: Finds all `.csx` files in the `src/` directory
2. **Cleaning**: Removes `#load` statements (including `#load "../../template/src/ScriptGlobals.csx"`)
3. **Encoding**: Encodes cleaned code to base64 format
4. **JSON Update**: Updates `rule.code` and `mapping.code` fields in workflow JSON files

> **Important**: The automation system automatically removes `#load` statements before encoding. This means you can use `ScriptGlobals.csx` for IntelliSense during development, and the automation will ensure clean production code.

## ⚙️ Configuration

### Tasks Configuration
Tasks defined in `.vscode/tasks.json`:

- `Update Workflow Rules`: Updates current directory
- `Update All Workflow Rules`: Updates all workflows
- `Watch Workflow Changes`: Monitors file changes

### Keyboard Shortcuts
Defined in `.vscode/keybindings.json`:

**Windows/Linux:**
- `Ctrl + Shift + W`: Single workflow update
- `Ctrl + Shift + Alt + W`: All workflows update
- `Ctrl + Shift + M`: Create new mapping file
- `Ctrl + Shift + R`: Create new rule file

**Mac:**
- `Cmd + Shift + W`: Single workflow update
- `Cmd + Shift + Alt + W`: All workflows update
- `Cmd + Shift + M`: Create new mapping file
- `Cmd + Shift + R`: Create new rule file

## 🛠️ Development Workflow

### Setting Up the Development Environment

1. **Install Node.js**: Ensure Node.js is installed on your system
2. **Open Project in VS Code**: Open the workflow project in VS Code
3. **Enable File Watcher**: Run the file watcher for automatic updates during development

### Creating New Workflows

#### Method 1: Using Templates (Recommended)

1. **Navigate to workflow directory**: Open your workflow's `src/` folder in VS Code
2. **Create mapping file**: Use `Ctrl + Shift + M` and enter filename (e.g., "AccountCreationMapping")
3. **Create rule file**: Use `Ctrl + Shift + R` and enter filename (e.g., "ValidationRule")
4. **Implement logic**: Fill in the TODO sections with your actual logic
5. **Update workflow**: Use `Ctrl + Shift + W` to update the JSON file

#### Method 2: Using Code Snippets

1. **Create new .csx file** in your workflow's `src/` directory
2. **Type trigger**: Type `wfmapping` for mapping or `wfrule` for rule
3. **Press Tab**: VS Code will generate the template with automatic class naming
4. **Implement logic**: Fill in your code where needed

#### Method 3: Manual Creation

1. **Create Directory Structure**:
   ```
   examples/my-new-workflow/
   ├── my-workflow-definition.json
   └── src/
       ├── MyRule.csx
       └── MyMapping.csx
   ```

2. **Write CSX Files**: Create your rule and mapping files with proper class structure
3. **Update Workflow**: Use `Ctrl + Shift + W` to update the JSON file
4. **Test**: Test your workflow using the API endpoints

### IntelliSense Support for CSX Files

The project includes a `ScriptGlobals.csx` file that provides IntelliSense support in VS Code for CSX development:

#### Using ScriptGlobals.csx

Add this line at the top of your CSX files:
```csharp
#load "../../template/src/ScriptGlobals.csx"
```

This provides:
- **NuGet Package References**: Access to required dependencies
- **Project Assembly References**: BBT.Workflow.Domain, Application, and Scripting assemblies
- **Using Statements**: Pre-imported namespaces for common workflow operations
- **IntelliSense**: Full code completion, syntax highlighting, and error detection

#### ScriptGlobals.csx Contents

The file includes essential references:
```csharp
// NuGet packages
#r "nuget: Microsoft.Extensions.DependencyInjection, 10.0.0"
#r "nuget: System.Text.Json, 10.0.0"
#r "nuget: Microsoft.Extensions.Caching.Distributed, 10.0.0"

// Project assemblies (built automatically)
#r "../../../src/BBT.Workflow.Domain/bin/Debug/net10.0/BBT.Workflow.Domain.dll"
#r "../../../src/BBT.Workflow.Application/bin/Debug/net10.0/BBT.Workflow.Application.dll"

// Common using statements
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
// ... and more
```

#### Template Files

Use the provided template files as starting points:
- **`examples/template/src/MappingTemplate.csx`**: Complete mapping class template
- **`examples/template/src/ScriptGlobals.csx`**: IntelliSense support file

#### IntelliSense Features Available

With ScriptGlobals.csx, you get:
- **Auto-completion** for workflow classes and methods
- **Parameter hints** for method signatures
- **Type checking** and error highlighting
- **Go to definition** for workflow types
- **Hover documentation** for APIs

### Best Practices

- **Use Template Creator**: Use `Ctrl + Shift + M` or `Ctrl + Shift + R` to create new files instead of copying templates
- **Use File Watcher**: Start file watcher during development for automatic updates
- **Enable IntelliSense**: Template creator automatically includes `#load` statements for IntelliSense support
- **Use Code Snippets**: Type `wfmapping` or `wfrule` and press Tab for quick template expansion
- **Consistent Naming**: Use PascalCase naming for class names (automatically handled by templates)
- **Test Regularly**: Test workflows after each significant change
- **Version Control**: Commit both CSX files and updated JSON files

## 🐛 Troubleshooting

### Script Not Working
- Ensure Node.js is installed
- Check version with `node --version` in terminal

### Files Not Updating
- Verify correct file structure
- Ensure JSON file contains the word `workflow`
- Confirm `src/` directory exists

### File Watcher Not Stopping
- Use `Ctrl + C` to stop
- Close from VS Code Terminal panel

## 📝 Examples

### Single Workflow Update
```bash
# Update checking account opening workflow
node .vscode/scripts/update-workflow-rules.js examples/checking-account-opening
```

### All Workflows Update
```bash
# Update all workflows in examples directory
node .vscode/scripts/update-all-workflows.js
```

### Start File Watching
```bash
# Start monitoring CSX file changes
node .vscode/scripts/watch-workflows.js
```

## 🎯 Tips

1. **File Watcher**: Run file watcher during development for automatic updates when you save files
2. **Keyboard Shortcuts**: Memorize keyboard shortcuts for frequent use
3. **Batch Updates**: Use bulk update after major changes
4. **Error Handling**: Script errors are displayed in terminal/output panel

## 🔄 Development Example Workflow

### Modern Workflow (Recommended)

1. **Navigate** to your workflow's `src/` directory in VS Code
2. **Create file** using `Ctrl + Shift + M` / `Cmd + Shift + M` (mapping) or `Ctrl + Shift + R` / `Cmd + Shift + R` (rule)
3. **Enter filename** when prompted (e.g., "AccountValidation")
4. **Write code** with full IntelliSense support (auto-completion, error checking)
5. **Save** with `Ctrl + S`
6. **Update** with `Ctrl + Shift + W` / `Cmd + Shift + W` (automation handles everything automatically)
7. **Verify** changes in JSON file
8. **Test** workflow using API endpoints

### Alternative: Using Code Snippets

1. **Create new .csx file** in your workflow's `src/` directory
2. **Type trigger** (`wfmapping`, `wfrule`, or `wfscript`)
3. **Press Tab** - template expands with automatic class naming
4. **Implement logic** in TODO sections
5. **Save and update** using `Ctrl + Shift + W` / `Cmd + Shift + W`

No more manual copying, renaming, or base64 encoding! 🎉

### IntelliSense Development Tips

- **Build project first**: Ensure assemblies are built for IntelliSense to work: `dotnet build`
- **Restart OmniSharp**: If IntelliSense stops working, use `Ctrl + Shift + P` (Windows/Linux) / `Cmd + Shift + P` (Mac) → "OmniSharp: Restart OmniSharp"
- **Check file associations**: Ensure `.csx` files are associated with C# in VS Code settings

## 🚀 Advanced Usage

### Custom Script Modifications

The automation scripts are located in `.vscode/scripts/` and can be customized:

- `update-workflow-rules.js`: Core update logic
- `update-all-workflows.js`: Batch processing
- `watch-workflows.js`: File monitoring

### Integration with CI/CD

The scripts can be integrated into build pipelines:

```bash
# Pre-build validation
npm run validate-workflows

# Update all workflows before deployment
node .vscode/scripts/update-all-workflows.js
```

### Performance Considerations

- **File Watching**: Includes debouncing to handle rapid file changes
- **Batch Processing**: Processes workflows in parallel where possible
- **Memory Efficient**: Scripts handle large workflows without memory issues

## 🔍 JSON Schema Validation System

BBT Workflow provides a comprehensive JSON Schema validation system for workflow definition files, offering real-time validation, auto-completion, and detailed error reporting in VS Code.

### Features

- **Real-time Validation**: Instant error detection as you type
- **IntelliSense Support**: Auto-completion with descriptions for all workflow properties
- **Schema Validation**: Validates against BBT Workflow JSON Schema
- **Business Logic Validation**: Checks state references, reachability, and duplicate keys
- **Detailed Error Reports**: Line-by-line error reporting with smart suggestions
- **Multiple File Format Support**: Supports various workflow file naming patterns

### VS Code Integration

#### Automatic Schema Association

The system automatically applies JSON Schema validation to files matching these patterns:

```json
{
  "json.schemas": [
    {
      "fileMatch": [
        "**/examples/**/*workflow*.json",
        "**/*workflow-definition*.json", 
        "**/*-workflow-*.json",
        "**/*-flow-*.json"
      ],
      "url": "./.vscode/schemas/workflow-definition.schema.json"
    }
  ]
}
```

#### IntelliSense Features

When editing workflow JSON files, you get:

- **Property Suggestions**: Auto-complete for all workflow properties
- **Enum Values**: Dropdown lists for predefined values (workflow types, state types, etc.)
- **Validation Messages**: Real-time error highlighting with explanations
- **Hover Documentation**: Detailed descriptions on hover
- **Format Validation**: Pattern validation for keys, versions, and language codes

### Schema Validation Rules

#### Workflow Types
- `C`: Core workflow
- `F`: Flow workflow  
- `S`: SubFlow workflow
- `P`: Sub Process workflow

#### State Types
- `1`: Initial state
- `2`: Intermediate state
- `3`: Finish state
- `4`: SubFlow state

#### Trigger Types
- `0`: Manual trigger
- `1`: Automatic trigger
- `2`: Timeout trigger
- `3`: Event trigger

#### Version Strategies
- `Major`: Major version changes
- `Minor`: Minor version changes
- `Patch`: Patch version changes

#### Pattern Validations
- **Keys**: Must match `^[a-z0-9-]+$` pattern
- **Versions**: Must follow semantic versioning `^\\d+\\.\\d+\\.\\d+$`
- **Language Codes**: Support both `en` and `en-US` formats
- **Domains**: Must match `^[a-z0-9-]+$` pattern

### Code Snippets

The system includes 20+ code snippets for rapid development:

#### Basic Workflow Structure
- `workflow-basic`: Complete basic workflow template
- `workflow-state-initial`: Initial state template
- `workflow-state-intermediate`: Intermediate state template  
- `workflow-state-final`: Final state template
- `workflow-transition-manual`: Manual transition template
- `workflow-transition-automatic`: Automatic transition template

#### Common Objects
- `labels`: Multi-language labels array
- `label`: Single language label
- `mapping`: Mapping object with location and code
- `rule`: Rule object with location and code
- `transitions`: Transitions array

#### Reference Objects
- `function-ref`: Function reference
- `extension-ref`: Extension reference
- `task-ref`: Task reference

#### Task Arrays
- `onentries`: OnEntries array
- `onexits`: OnExits array
- `onexecutiontasks`: OnExecutionTasks array

#### Collections
- `functions`: Functions array
- `extensions`: Extensions array

### Validation Tools

#### Command Line Validation

```bash
# Validate specific file
npm run validate examples/workflow-example.json

# Validate all workflow files
npm run validate:all

# Watch for changes and validate continuously
npm run validate:watch
```

#### VS Code Tasks

Access validation through VS Code Command Palette (`Ctrl+Shift+P`):

- **Validate Workflow JSON**: Validates specific file or directory
- **Validate All Workflow JSONs**: Validates all files in examples directory

#### Validation Features

✅ **JSON Schema Validation**: Complete structural validation  
✅ **Business Logic Validation**: State reference checking, reachability analysis  
✅ **Parse Error Handling**: Smart JSON syntax error detection with visual pointers  
✅ **Duplicate Key Detection**: Identifies duplicate keys across the workflow  
✅ **Unreachable State Detection**: Finds states that cannot be reached  
✅ **Line-by-Line Error Reporting**: Precise error location with line and column numbers  
✅ **Smart Suggestions**: Context-aware error resolution suggestions  
✅ **Colored Output**: Terminal output with color coding for better readability  

### Error Types and Solutions

#### Schema Validation Errors
- **Missing Required Properties**: Shows which required fields are missing
- **Invalid Property Values**: Explains valid values for enum properties
- **Pattern Mismatches**: Provides correct format examples for keys, versions, etc.
- **Type Mismatches**: Shows expected data types

#### Business Logic Errors
- **Invalid State References**: Detects references to non-existent states
- **Unreachable States**: Identifies states with no incoming transitions
- **Duplicate Keys**: Finds duplicate keys in states, transitions, functions, etc.
- **Circular Dependencies**: Detects potential circular references

#### Parse Errors
- **JSON Syntax Errors**: Visual pointer showing exact error location
- **Missing Brackets/Braces**: Smart detection of unclosed structures
- **Trailing Commas**: Detection of invalid trailing commas
- **Quote Mismatches**: Identification of unmatched quotes

### Advanced Validation Configuration

#### AJV Configuration
The validation system uses AJV (Another JSON Schema Validator) with:

```javascript
const ajv = new Ajv({
  strict: false,           // Allow enumDescriptions
  allErrors: true,         // Show all validation errors
  verbose: true,           // Detailed error information
  validateFormats: true    // Validate format constraints
});
```

#### Custom Keywords
- **enumDescriptions**: Enhanced enum validation with descriptions
- **Pattern Validation**: Custom regex patterns for BBT Workflow specific formats
- **Cross-Reference Validation**: Custom validation for state and transition references

### Setup Instructions

#### Prerequisites
```bash
# Install Node.js dependencies
npm install
```

#### Dependencies
- `ajv`: JSON Schema validation engine
- `ajv-formats`: Additional format validators
- `nodemon`: File watching for continuous validation

#### VS Code Extensions
The system works with standard VS Code JSON support, no additional extensions required.

### Usage Examples

#### Creating a New Workflow

1. **Create workflow file**: Name it following the pattern `*workflow*.json`
2. **Start typing**: VS Code automatically provides schema validation
3. **Use snippets**: Type `workflow-basic` and press Tab for a complete template
4. **Validate**: Run `npm run validate filename.json` for detailed validation

#### Fixing Validation Errors

1. **Check VS Code Problems Panel**: Shows real-time validation errors
2. **Use hover tooltips**: Hover over red underlined text for error details
3. **Run command line validator**: Get detailed error reports with line numbers
4. **Follow suggestions**: Each error includes context-aware suggestions

#### Continuous Development

1. **Start file watcher**: `npm run validate:watch`
2. **Edit workflow files**: Real-time validation as you type
3. **Check terminal output**: Colored validation results
4. **Fix errors incrementally**: Address issues as they appear

### Best Practices

#### File Organization
- Use consistent naming patterns (`*workflow*.json`)
- Keep workflow files in dedicated directories
- Use descriptive filenames that include "workflow" or "flow"

#### Development Workflow
1. **Start with snippets**: Use provided code snippets for consistent structure
2. **Validate frequently**: Run validation after major changes
3. **Fix errors incrementally**: Address validation errors as they appear
4. **Test thoroughly**: Validate complete workflows before deployment

#### Error Resolution
- **Read error messages carefully**: They include specific location and context
- **Check referenced objects**: Ensure all state/transition references exist
- **Validate patterns**: Ensure keys, versions, and codes follow required formats
- **Use schema documentation**: Refer to schema for valid values and structures

### Troubleshooting

#### Schema Not Loading
- Verify file naming matches patterns in `settings.json`
- Restart VS Code if schema changes are not recognized
- Check for syntax errors in `settings.json`

#### Validation Errors
- Ensure Node.js is installed for command-line validation
- Check that all required dependencies are installed (`npm install`)
- Verify file paths in validation commands

#### Performance Issues
- Large files may cause slower validation
- Use `validate:watch` sparingly with many files
- Consider validating specific directories instead of entire projects

## 📚 Related Documentation

- [Scripting Engine](./scripting-engine.md) - Understanding CSX script execution
- [Examples](./examples/) - Sample workflow implementations
- [Task Executors](./task-executors.md) - Task execution system
- [Getting Started](./getting-started.md) - Initial setup and configuration 