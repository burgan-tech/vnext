#!/bin/bash

# Wait for VNext App to get healthy
echo "Waiting for vnext-app to be healthy..."

VNEXT_APP_URL=${VNEXT_APP_URL:-"http://host.docker.internal:4201"}
API_ENDPOINT="${VNEXT_APP_URL}/api/v1/admin/publish"

# Get APP_DOMAIN from environment variable, default to "core"
APP_DOMAIN=${APP_DOMAIN:-"core"}
echo "Using APP_DOMAIN: $APP_DOMAIN"

# Wait for the vnext-app to be ready with health check
while true; do
    if curl -s "${VNEXT_APP_URL}/health" > /dev/null 2>&1; then
        echo "VNext App is healthy!"
        break
    else
        echo "VNext App is not ready yet, waiting..."
        sleep 5
    fi
done

# Find the path to the core runtime package
CORE_PATH="/app/node_modules/@burgan-tech/vnext-core-runtime/core"
CUSTOM_PATH="/app/custom-components"

if [ ! -d "$CORE_PATH" ]; then
    echo "Error: vnext-core-runtime package not found at $CORE_PATH"
    exit 1
fi

echo "Found vnext-core-runtime at: $CORE_PATH"

# Check if jq is available
echo "Checking for required tools..."
if ! command -v jq >/dev/null 2>&1; then
    echo "✗ ERROR: jq command not found. Please install jq."
    exit 1
fi
echo "✓ jq is available: $(jq --version)"

# Check if custom components directory exists
if [ -d "$CUSTOM_PATH" ]; then
    echo "Found custom components directory at: $CUSTOM_PATH"
else
    echo "Custom components directory not found, using only core components"
fi

# Function to replace domain values in JSON file with specific rules
replace_domain_in_json() {
    local input_file="$1"
    local output_file="$2"
    local target_domain="$3"
    
    # Check if input file exists and is readable
    if [ ! -f "$input_file" ]; then
        echo "✗ Error: Input file not found: $input_file"
        return 1
    fi
    
    if [ ! -r "$input_file" ]; then
        echo "✗ Error: Input file not readable: $input_file"
        return 1
    fi
    
    # Check if file has content
    if [ ! -s "$input_file" ]; then
        echo "✗ Error: Input file is empty: $input_file"
        return 1
    fi
    
    echo "  - Replacing domain values with '$target_domain' in $(basename "$input_file") (with specific rules)"
    
    # Use jq with specific rules for domain replacement
    if ! jq --arg domain "$target_domain" '
        def should_replace_domain:
            # Replace domain if it has key, flow, version, domain fields
            # and domain is a string
            if type == "object" and has("domain") and (.domain | type) == "string" then
                if has("key") and has("flow") and has("version") then
                    true
                else
                    false
                end
            else
                false
            end;
        
        def replace_domain_selective:
            if type == "object" then
                # Replace domain if it matches the criteria
                if should_replace_domain then
                    .domain = $domain
                else
                    .
                end |
                # Recursively process all object properties
                with_entries(.value |= replace_domain_selective)
            elif type == "array" then
                # For arrays, apply the same logic to each item
                map(replace_domain_selective)
            else
                .
            end;
        
        replace_domain_selective
    ' "$input_file" > "$output_file"; then
        echo "✗ Error: Failed to replace domain values in $(basename "$input_file")"
        return 1
    fi
    
    # Verify the output file is valid JSON
    if ! jq '.' "$output_file" >/dev/null 2>&1; then
        echo "✗ Error: Output file is not valid JSON after domain replacement"
        return 1
    fi
    
    echo "  - Successfully replaced domain values in $(basename "$input_file") with selective rules"
    return 0
}

# Function to upload a JSON file to the API
upload_json_file() {
    local file_path="$1"
    local file_description="$2"
    
    # Check if file exists and is readable
    if [ ! -f "$file_path" ]; then
        echo "✗ Error: File not found: $file_path"
        return 1
    fi
    
    if [ ! -r "$file_path" ]; then
        echo "✗ Error: File not readable: $file_path"
        return 1
    fi
    
    # Check if file has content
    if [ ! -s "$file_path" ]; then
        echo "✗ Error: File is empty: $file_path"
        return 1
    fi
    
    echo "Uploading $file_description: $(basename "$file_path")"
    echo "  File path: $file_path"
    echo "  File size: $(wc -c < "$file_path") bytes"
    
    # Read the contents of the JSON file and send it to the API
    response=$(curl -X POST \
        -H "Content-Type: application/json" \
        -d @"$file_path" \
        "$API_ENDPOINT" \
        --max-time 30 \
        --retry 3 \
        --write-out "HTTPSTATUS:%{http_code}" \
        --silent)
    
    # Extract HTTP status code and response body
    http_code=$(echo "$response" | grep -o "HTTPSTATUS:[0-9]*" | cut -d: -f2)
    response_body=$(echo "$response" | sed -E 's/HTTPSTATUS:[0-9]*$//')
    
    # Ensure http_code is not empty and is numeric
    if [ -z "$http_code" ]; then
        echo "✗ Failed to get HTTP status code from response"
        echo "  Raw response: $response"
        return 1
    fi
    
    # Check if http_code is numeric
    if ! [[ "$http_code" =~ ^[0-9]+$ ]]; then
        echo "✗ Invalid HTTP status code: '$http_code'"
        echo "  Raw response: $response"
        return 1
    fi
    
    if [ "$http_code" -eq 200 ] || [ "$http_code" -eq 201 ]; then
        echo "✓ Successfully uploaded: $(basename "$file_path") (HTTP $http_code)"
        if [ -n "$response_body" ] && [ "$response_body" != "" ]; then
            echo "  Response: $response_body"
        fi
        return 0
    else
        echo "✗ Failed to upload: $(basename "$file_path") (HTTP $http_code)"
        if [ -n "$response_body" ] && [ "$response_body" != "" ]; then
            echo "  Error details: $response_body"
        fi
        echo "  Raw response: $response"
        return 1
    fi
}

# Function to merge custom components with core sys-* file
merge_custom_components() {
    # Redirect all output to stderr except the final result
    {
    local sys_file="$1"
    local custom_folder="$2"
    local folder_name="$3"
    local temp_file="/tmp/merged_$(basename "$sys_file")"
    
    # Check if /tmp directory is writable, if not use alternative location
    if [ ! -w "/tmp" ]; then
        echo "DEBUG: /tmp not writable, using alternative location"
        temp_file="/app/merged_$(basename "$sys_file")"
    fi
    echo "DEBUG: Using temp file: $temp_file"
    
    # Check if sys_file exists
    echo "DEBUG: Checking if sys_file exists: $sys_file"
    if [ ! -f "$sys_file" ]; then
        echo "✗ Error: Core sys file not found: $sys_file"
        return 1
    fi
    echo "DEBUG: sys_file exists and is readable"
    
    echo "Starting merge process for $(basename "$sys_file")"
    echo "  Core file: $sys_file"
    echo "  Custom folder: $custom_folder"
    
    # Copy core sys-* file as base
    echo "DEBUG: Copying sys_file to temp_file"
    if ! cp "$sys_file" "$temp_file"; then
        echo "✗ Error: Failed to copy $sys_file to $temp_file"
        return 1
    fi
    echo "DEBUG: Copy successful, temp_file: $temp_file"
    
    # Verify the copied file is valid JSON
    echo "DEBUG: Verifying copied file is valid JSON"
    if ! jq '.' "$temp_file" >/dev/null 2>&1; then
        echo "✗ Error: Copied file is not valid JSON: $temp_file"
        echo "DEBUG: File contents:"
        head -10 "$temp_file"
        return 1
    fi
    echo "DEBUG: Copied file is valid JSON"
    
    # Check if the core file has a data array, if not create one
    echo "DEBUG: Checking for data array in core file"
    if ! jq -e '.data' "$temp_file" >/dev/null 2>&1; then
        echo "  - Core file missing 'data' array, creating empty array"
        if ! jq '. + {"data": []}' "$temp_file" > "${temp_file}.tmp"; then
            echo "✗ Error: Failed to create data array"
            return 1
        fi
        if ! mv "${temp_file}.tmp" "$temp_file"; then
            echo "✗ Error: Failed to move temporary file"
            return 1
        fi
        echo "DEBUG: Successfully created empty data array"
    else
        echo "DEBUG: Core file already has data array"
    fi
    
    # Get initial data count
    local initial_count
    initial_count=$(jq '.data | length' "$temp_file" 2>/dev/null || echo "0")
    echo "  - Core file has $initial_count existing data items"
    echo "  - Base file size: $(wc -c < "$temp_file") bytes"
    
    # Check if custom components exist for this folder
    if [ -d "$custom_folder" ]; then
        echo "  - Scanning custom components in $custom_folder"
        
        # Use for loop to avoid subshell issues
        local custom_files_found=false
        local total_custom_items=0
        
        for custom_file in "$custom_folder"/*.json; do
            # Check if the glob pattern matched any files
            if [ -f "$custom_file" ]; then
                custom_files_found=true
                echo "    • Processing: $(basename "$custom_file")"
                
                # Extract data array from custom file
                local custom_data
                if ! custom_data=$(jq '.data' "$custom_file" 2>/dev/null); then
                    echo "      ✗ Failed to parse JSON in $(basename "$custom_file")"
                    continue
                fi
                
                # Check if custom_data is valid and not empty
                if [ "$custom_data" != "null" ] && [ "$custom_data" != "[]" ] && [ "$custom_data" != "" ]; then
                    local item_count
                    if item_count=$(jq '. | length' <<< "$custom_data" 2>/dev/null); then
                        if [ "$item_count" -gt 0 ]; then
                            echo "      → Found $item_count custom items to merge"
                            
                            # Ensure data array exists in temp file and merge
                            if jq --argjson custom_data "$custom_data" '
                                if .data then 
                                    .data += $custom_data 
                                else 
                                    . + {"data": $custom_data}
                                end' "$temp_file" > "${temp_file}.tmp"; then
                                mv "${temp_file}.tmp" "$temp_file"
                                total_custom_items=$((total_custom_items + item_count))
                                echo "      ✓ Successfully merged $item_count items"
                            else
                                echo "      ✗ Failed to merge data from $(basename "$custom_file")"
                                rm -f "${temp_file}.tmp"
                            fi
                        else
                            echo "      - Empty data array in $(basename "$custom_file")"
                        fi
                    else
                        echo "      ✗ Could not count items in $(basename "$custom_file")"
                    fi
                else
                    echo "      - No valid data array in $(basename "$custom_file")"
                fi
            fi
        done
        
        if [ "$custom_files_found" = false ]; then
            echo "    - No JSON files found in $custom_folder"
        else
            echo "  - Total custom items merged: $total_custom_items"
        fi
    else
        echo "  - No custom components folder found: $custom_folder"
        echo "  - Using only core sys-* file"
    fi
    
    # Final verification and summary
    local final_count
    final_count=$(jq '.data | length' "$temp_file" 2>/dev/null || echo "0")
    local added_items=$((final_count - initial_count))
    
    echo "  - Final data array count: $final_count items (added: $added_items)"
    echo "  - Final merged file size: $(wc -c < "$temp_file") bytes"
    
    # Final validation
    echo "DEBUG: Final validation of merged file"
    if [ ! -f "$temp_file" ]; then
        echo "✗ Error: Temp file does not exist after merge: $temp_file"
        return 1
    fi
    
    if ! jq '.' "$temp_file" >/dev/null 2>&1; then
        echo "✗ Error: Final merged file is not valid JSON"
        return 1
    fi
    
    echo "  - Merge completed successfully"
    
    # Apply domain replacement if APP_DOMAIN is set and different from "core"
    if [ -n "$APP_DOMAIN" ] && [ "$APP_DOMAIN" != "core" ]; then
        echo "  - Applying domain replacement: core -> $APP_DOMAIN"
        local domain_replaced_file="${temp_file}.domain_replaced"
        
        if replace_domain_in_json "$temp_file" "$domain_replaced_file" "$APP_DOMAIN"; then
            # Replace the original temp file with domain-replaced version
            if mv "$domain_replaced_file" "$temp_file"; then
                echo "  - Domain replacement completed successfully"
            else
                echo "✗ Error: Failed to replace temp file with domain-replaced version"
                rm -f "$domain_replaced_file"
                return 1
            fi
        else
            echo "✗ Error: Domain replacement failed"
            rm -f "$domain_replaced_file"
            return 1
        fi
    else
        echo "  - Skipping domain replacement (APP_DOMAIN=$APP_DOMAIN)"
    fi
    
    echo "DEBUG: Returning temp file path: $temp_file"
    
    # Close stderr redirection and return only the file path to stdout
    } >&2
    
    # Return only the temp file path to stdout (this is what gets captured)
    echo "$temp_file"
}

# Define sys-* files mapping to their corresponding custom components folders
# All sys-* files are located in the core/Workflows folder
declare -A SYS_FILE_MAPPINGS=(
    ["sys-flows.json"]="Workflows"
    ["sys-tasks.json"]="Tasks"
    ["sys-extensions.json"]="Extensions"
    ["sys-functions.json"]="Functions"
    ["sys-views.json"]="Views"
    ["sys-schemas.json"]="Schemas"
)

# Core workflows path where all sys-* files are located
WORKFLOWS_PATH="$CORE_PATH/Workflows"

echo "=========================================="
echo "VNext Core Runtime Package Loading"
echo "=========================================="
echo "Core path: $WORKFLOWS_PATH"
echo "Custom components path: $CUSTOM_PATH"
echo ""

echo "=========================================="
echo "Step 1: Loading CRITICAL sys-flows first (PRIORITY)"
echo "=========================================="

# STEP 1: Process sys-flows.json FIRST (CRITICAL PRIORITY)
SYS_FLOWS_FILE="$WORKFLOWS_PATH/sys-flows.json"

if [ -f "$SYS_FLOWS_FILE" ]; then
    echo "Processing CRITICAL core file: sys-flows.json"
    echo "  Core file: $SYS_FLOWS_FILE"
    
    # Merge with custom Workflows components
    CUSTOM_WORKFLOWS_PATH="$CUSTOM_PATH/Workflows"
    echo "  Custom components folder: $CUSTOM_WORKFLOWS_PATH"
    
    merged_sys_flows=$(merge_custom_components "$SYS_FLOWS_FILE" "$CUSTOM_WORKFLOWS_PATH" "Workflows")
    merge_exit_code=$?
    
    # Check if merge was successful
    echo "DEBUG: merge_custom_components returned exit code: $merge_exit_code"
    echo "DEBUG: merged_sys_flows value: '$merged_sys_flows'"
    
    if [ $merge_exit_code -ne 0 ]; then
        echo "✗ CRITICAL ERROR: merge_custom_components function failed with exit code $merge_exit_code"
        echo "  This is a critical error that may affect system functionality!"
        exit 1
    elif [ -z "$merged_sys_flows" ]; then
        echo "✗ CRITICAL ERROR: merge_custom_components returned empty result"
        echo "  This is a critical error that may affect system functionality!"
        exit 1
    elif [ ! -f "$merged_sys_flows" ]; then
        echo "✗ CRITICAL ERROR: Merged file does not exist: $merged_sys_flows"
        echo "  This is a critical error that may affect system functionality!"
        exit 1
    else
        # Upload the merged sys-flows.json
        echo ""
        echo "Uploading CRITICAL sys-flows.json..."
        if upload_json_file "$merged_sys_flows" "CRITICAL sys-flows"; then
            echo "✓ CRITICAL SUCCESS: sys-flows.json uploaded successfully"
        else
            echo "✗ CRITICAL ERROR: Failed to upload sys-flows.json"
            echo "  This is a critical error that may affect system functionality!"
            rm -f "$merged_sys_flows"
            exit 1
        fi
    fi
    
    # Clean up temporary file
    rm -f "$merged_sys_flows"
    
    # Wait for rate limiting
    sleep 2
else
    echo "✗ CRITICAL ERROR: sys-flows.json not found in $WORKFLOWS_PATH"
    echo "  Cannot proceed without sys-flows.json!"
    exit 1
fi

echo ""
echo "=========================================="
echo "Step 2: Loading all other sys-* system components"
echo "=========================================="

# STEP 2: Process all other sys-* files in order
# Process in a specific order for dependencies
SYS_FILES_ORDER=("sys-tasks.json" "sys-extensions.json" "sys-functions.json" "sys-views.json" "sys-schemas.json")

for sys_file_name in "${SYS_FILES_ORDER[@]}"; do
    custom_folder_name="${SYS_FILE_MAPPINGS[$sys_file_name]}"
    
    SYS_FILE_PATH="$WORKFLOWS_PATH/$sys_file_name"
    CUSTOM_FOLDER_PATH="$CUSTOM_PATH/$custom_folder_name"
    
    echo ""
    echo "Processing: $sys_file_name"
    echo "  Core file: $SYS_FILE_PATH"
    echo "  Custom folder: $CUSTOM_FOLDER_PATH"
    echo "------------------------------------------"
    
    if [ -f "$SYS_FILE_PATH" ]; then
        echo "✓ Found core sys file: $sys_file_name"
        
        # Merge with custom components
        merged_file=$(merge_custom_components "$SYS_FILE_PATH" "$CUSTOM_FOLDER_PATH" "$custom_folder_name")
        
        # Check if merge was successful
        if [ -z "$merged_file" ] || [ ! -f "$merged_file" ]; then
            echo "✗ Failed to merge $sys_file_name"
            continue
        fi
        
        # Upload the merged file
        echo ""
        echo "Uploading $sys_file_name..."
        if upload_json_file "$merged_file" "$sys_file_name"; then
            echo "✓ Successfully processed: $sys_file_name"
        else
            echo "✗ Failed to upload: $sys_file_name"
            # Continue with other files
        fi
        
        # Clean up temporary file
        rm -f "$merged_file"
        
        # Wait for rate limiting
        sleep 1
    else
        echo "⚠ Warning: $sys_file_name not found in core package"
        echo "  Skipping $sys_file_name (file does not exist in vnext-core-runtime)"
    fi
done

echo ""
echo "=========================================="
echo "Step 3: Loading additional workflow files"
echo "=========================================="

# STEP 3: Process any additional workflow files (excluding sys-* files)
if [ -d "$WORKFLOWS_PATH" ]; then
    echo "Checking for additional workflow files..."
    
    # Find all JSON files that are NOT sys-* files
    additional_files_found=false
    for json_file in "$WORKFLOWS_PATH"/*.json; do
        if [ -f "$json_file" ]; then
            filename=$(basename "$json_file")
            
            # Skip sys-* files as they are already processed
            if [[ "$filename" == sys-*.json ]]; then
                continue
            fi
            
            additional_files_found=true
            echo ""
            echo "Processing additional workflow file: $filename"
            
            # For additional files, merge with Workflows custom components
            CUSTOM_WORKFLOWS_PATH="$CUSTOM_PATH/Workflows"
            merged_file=$(merge_custom_components "$json_file" "$CUSTOM_WORKFLOWS_PATH" "Workflows")
            
            # Check if merge was successful
            if [ -z "$merged_file" ] || [ ! -f "$merged_file" ]; then
                echo "✗ Failed to merge $filename"
                continue
            fi
            
            # Upload the file
            if upload_json_file "$merged_file" "additional workflow"; then
                echo "✓ Successfully uploaded: $filename"
            else
                echo "✗ Failed to upload: $filename"
            fi
            
            # Clean up temporary file
            rm -f "$merged_file"
            
            # Wait for rate limiting
            sleep 1
        fi
    done
    
    if [ "$additional_files_found" = false ]; then
        echo "No additional workflow files found (only sys-* files processed)"
    fi
fi

echo ""
echo "=========================================="
echo "✓ VNext Core Runtime Initialization COMPLETED!"
echo "=========================================="
echo "All system components have been successfully loaded."

# Infinite loop to prevent the container from closing (optional)
# tail -f /dev/null
# tail -f /dev/null
