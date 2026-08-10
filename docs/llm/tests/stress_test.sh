#!/bin/bash

# ==========================================
# ASSUMPTIONS & DEPENDENCIES
# ==========================================
# 1. OS: Linux (Fedora/Ubuntu) or macOS
# 2. Required Binaries: bash, curl, xargs, awk, sort, seq, date, mktemp
# 3. CPU/Process Limit: High concurrency (e.g., >1000) requires sufficient 
#    'ulimit -u' (max user processes) and 'ulimit -n' (open file descriptors).

# ==========================================
# CONFIGURATION VARIABLES
# ==========================================

URLS=(
    "https://rider-nevada-keeping-judicial.trycloudflare.com/healthz/live"
    "https://rider-nevada-keeping-judicial.trycloudflare.com/healthz/ready"
)

# Output log file (Created in the same folder as this script)
OUTPUT_FILENAME="stress_test_details.log"

# Testing parameters
TOTAL_REQUESTS=100000
CONCURRENT_REQUESTS=2000

# Resilience: Prevent hanging processes when the server is overwhelmed
TIMEOUT_CONNECT=5  # Max seconds to wait for a TCP connection
TIMEOUT_MAX=10     # Max seconds a single request is allowed to take

# ==========================================
# SCRIPT LOGIC
# ==========================================

# Resolve the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$SCRIPT_DIR/$OUTPUT_FILENAME"

# Ensure temp files are cleaned up if the user hits Ctrl+C (SIGINT)
trap 'echo -e "\n[!] Test aborted by user. Cleaning up..."; rm -f "$TEMP_FILE" "$SORTED_FILE"; exit 1' INT TERM

echo "==================================================="
echo " Starting Advanced Stress Test"
echo " Target Requests: $TOTAL_REQUESTS @ $CONCURRENT_REQUESTS concurrency"
echo " Detailed logs: $LOG_FILE"
echo "==================================================="
echo ""

for URL in "${URLS[@]}"; do
    echo "Testing URL: $URL ..."
    
    # Create temp files for raw and sorted metrics
    TEMP_FILE=$(mktemp)
    SORTED_FILE=$(mktemp)
    
    # Record start time
    START_TIME=$(date +%s.%N)
    
    # Fire off the requests. 
    # Added --connect-timeout, --max-time, and Keep-Alive headers
    seq "$TOTAL_REQUESTS" | xargs -P "$CONCURRENT_REQUESTS" -I{} \
        curl -s -o /dev/null \
        -w "%{http_code} %{time_total}\n" \
        -H "Connection: keep-alive" \
        --connect-timeout "$TIMEOUT_CONNECT" \
        --max-time "$TIMEOUT_MAX" \
        "$URL" > "$TEMP_FILE"
    
    # Record end time
    END_TIME=$(date +%s.%N)
    
    # Sort the temp file numerically by the second column (time_total).
    # This enables us to calculate precise percentiles (p50, p95, p99).
    sort -k2 -n "$TEMP_FILE" > "$SORTED_FILE"
    
    # 1. Format and append the details to the permanent log file
    echo "=== Run Date: $(date) | URL: $URL ===" >> "$LOG_FILE"
    awk '{
        desc = ($1 == "000") ? "(Timeout/Drop)" : "";
        print "Status: " $1 " " desc " - Total Time: " $2 "s"
    }' "$SORTED_FILE" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"
    
    # 2. Process metrics and generate terminal summary using the sorted data
    awk -v start="$START_TIME" -v end="$END_TIME" -v reqs="$TOTAL_REQUESTS" -v conc="$CONCURRENT_REQUESTS" '
        BEGIN {
            count = 0;
            sum = 0;
            successes = 0;
        }
        {
            count++;
            code = $1;
            time = $2;
            
            # Track occurrences of each HTTP status code
            status_codes[code]++;
            
            if (code >= 200 && code < 300) successes++;
            
            sum += time;
            latencies[count] = time;
        }
        END {
            duration = end - start;
            if (duration <= 0) duration = 0.0001; # Prevent divide by zero
            
            rps = count / duration;
            avg = sum / count;
            
            # Extract Percentiles from sorted array
            min = latencies[1];
            max = latencies[count];
            p50 = latencies[int(count * 0.50) == 0 ? 1 : int(count * 0.50)];
            p95 = latencies[int(count * 0.95) == 0 ? 1 : int(count * 0.95)];
            p99 = latencies[int(count * 0.99) == 0 ? 1 : int(count * 0.99)];
            
            printf "  -> Total Requests:  %d\n", count;
            printf "  -> Concurrency:     %d\n", conc;
            printf "  -> Total Run Time:  %.2fs\n", duration;
            printf "  -> Req / Second:    %.2f RPS\n\n", rps;
            
            printf "  [ HTTP Status Codes ]\n";
            for (c in status_codes) {
                # 000 is curl standard for "failed to connect or timed out"
                label = (c == "000") ? " (Connection Drop/Timeout)" : "";
                printf "    - HTTP %s: %d%s\n", c, status_codes[c], label;
            }
            
            printf "\n  [ Latency Distribution ]\n";
            printf "    - Min:     %.3fs\n", min;
            printf "    - P50:     %.3fs (Median)\n", p50;
            printf "    - Average: %.3fs\n", avg;
            printf "    - P95:     %.3fs\n", p95;
            printf "    - P99:     %.3fs\n", p99;
            printf "    - Max:     %.3fs\n\n", max;
        }
    ' "$SORTED_FILE"
    
    # Clean up temp files
    rm -f "$TEMP_FILE" "$SORTED_FILE"
done

echo "Testing complete."
