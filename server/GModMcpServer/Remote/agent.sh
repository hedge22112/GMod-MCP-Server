# GMod MCP remote agent. SshAgent starts this over one `ssh` session and drives it
# through stdin/stdout, so the remote end needs only bash 4+ and coreutils: no .NET,
# no daemon, nothing installed. It serves file operations on garrysmod/data, which
# is all the file bridge is.
#
# Invoked as: bash -c "<this script>" gmodmcp-agent <base64 data path, or ->
#
# Commands arrive one per line on stdin, space-separated, with any free text base64:
#   put TAG REL B64        write REL atomically (.tmp + mv)        -> ok TAG | err TAG B64
#   rm TAG REL             delete REL                              -> ok TAG
#   read TAG REL OFF MAX   up to MAX bytes from OFF (OFF<0 = tail) -> data TAG LEN START B64|-
#   stat TAG REL                                                   -> stat TAG SIZE MTIME | stat TAG -
#   proc TAG               srcds processes, one per line           -> proc TAG B64|-
#   watch REL / unwatch REL   one-shot: emit the file once it exists and is non-empty
#   track REL / untrack REL   emit the file (or - when absent) whenever its checksum changes
# Output lines all start with "@@M " so anything a login script prints is ignored.

set -u
set -f

P='@@M'

emit() { printf '%s %s\n' "$P" "$*"; }
b64s() { printf '%s' "$1" | base64 -w0; }
fatal() { emit fatal "$(b64s "$1")"; exit 1; }

[ "${BASH_VERSINFO[0]:-0}" -ge 4 ] || fatal "the remote agent needs bash 4 or newer"
for tool in base64 cksum stat tail head find; do
    command -v "$tool" >/dev/null 2>&1 || fatal "'$tool' is not available on the remote host"
done

DATA=''
if [ "${1:--}" != '-' ]; then
    DATA=$(printf '%s' "$1" | base64 -d) || fatal "could not decode the data path argument"
fi
case $DATA in
    '~') DATA=$HOME ;;
    '~/'*) DATA="$HOME/${DATA#\~/}" ;;
esac

# No path given: find the one garrysmod/data that has run this addon (it writes
# mcp/manifest_server.json), falling back to any garrysmod/data. Ambiguity is an
# error rather than a guess, since picking the wrong server is silent.
discover() {
    local roots=("$HOME") found
    found=$(find "${roots[@]}" -maxdepth 8 -type f -path '*/garrysmod/data/mcp/manifest_server.json' 2>/dev/null | sed 's#/mcp/manifest_server\.json$##' | sort -u)
    if [ -z "$found" ]; then
        found=$(find "${roots[@]}" -maxdepth 8 -type d -path '*/garrysmod/data' 2>/dev/null | sort -u)
    fi
    if [ -z "$found" ]; then
        found=$(find /home /srv /opt -maxdepth 7 -type f -path '*/garrysmod/data/mcp/manifest_server.json' 2>/dev/null | sed 's#/mcp/manifest_server\.json$##' | sort -u)
    fi
    printf '%s' "$found"
}

if [ -z "$DATA" ]; then
    found=$(discover)
    count=$(printf '%s\n' "$found" | grep -c .)
    if [ "$count" -eq 0 ]; then
        fatal "no garrysmod/data folder found under $HOME, /home, /srv or /opt; pass the path as host:/path/to/garrysmod/data"
    elif [ "$count" -gt 1 ]; then
        fatal "several garrysmod/data folders found, pass one as host:/path/to/garrysmod/data: $(printf '%s' "$found" | tr '\n' ' ')"
    fi
    DATA=$found
fi

cd -- "$DATA" 2>/dev/null || fatal "cannot open $DATA on the remote host"
DATA=$(pwd -P)
case $DATA in
    */garrysmod/data) ;;
    *) fatal "$DATA is not a garrysmod/data folder" ;;
esac
mkdir -p mcp/server/in mcp/server/out mcp/client/in mcp/client/out 2>/dev/null

emit hello "$(b64s "$DATA")" "$(b64s "$(uname -sr 2>/dev/null)")"

declare -A WATCH=()
declare -A TRACK=()

emit_file() { # kind rel
    printf '%s %s %s ' "$P" "$1" "$2"
    base64 -w0 < "$2" 2>/dev/null
    printf '\n'
}

# srcds processes running out of this install (cwd = the game root), as
# "pid<TAB>elapsed seconds<TAB>command line".
list_procs() {
    local root pid comm cwd out=''
    root=$(cd -- "$DATA/../.." && pwd -P)
    set +f
    for dir in /proc/[0-9]*; do
        pid=${dir#/proc/}
        comm=$(cat "$dir/comm" 2>/dev/null) || continue
        case $comm in srcds_linux|srcds_linux64|srcds) ;; *) continue ;; esac
        cwd=$(readlink "$dir/cwd" 2>/dev/null)
        [ "$cwd" = "$root" ] || continue
        out+="$pid"$'\t'"$(ps -o etimes= -p "$pid" 2>/dev/null | tr -d ' ')"$'\t'"$(tr '\0' ' ' < "$dir/cmdline" 2>/dev/null)"$'\n'
    done
    set -f
    printf '%s' "$out"
}

handle() {
    # shellcheck disable=SC2086 # fields never contain spaces; globbing is off
    set -- $1
    local cmd=${1:-} tag=${2:-} rel=${3:-}
    case $cmd in
        put)
            mkdir -p -- "$(dirname -- "$rel")" 2>/dev/null
            if printf '%s' "${4:-}" | base64 -d > "$rel.tmp" 2>/dev/null && mv -f -- "$rel.tmp" "$rel"; then
                emit ok "$tag"
            else
                rm -f -- "$rel.tmp"
                emit err "$tag" "$(b64s "could not write $rel")"
            fi
            ;;
        rm)
            rm -f -- "$rel"
            emit ok "$tag"
            ;;
        read)
            local off=${4:-0} max=${5:-0} len
            if [ ! -f "$rel" ]; then emit data "$tag" -1 0 -; return; fi
            len=$(stat -c %s -- "$rel")
            if [ "$off" -lt 0 ]; then
                off=$((len + off))
                [ "$off" -lt 0 ] && off=0
            fi
            if [ "$off" -ge "$len" ] || [ "$max" -le 0 ]; then emit data "$tag" "$len" "$off" -; return; fi
            printf '%s data %s %s %s ' "$P" "$tag" "$len" "$off"
            tail -c +"$((off + 1))" -- "$rel" 2>/dev/null | head -c "$max" | base64 -w0
            printf '\n'
            ;;
        stat)
            if [ -e "$rel" ]; then emit stat "$tag" $(stat -c '%s %Y' -- "$rel"); else emit stat "$tag" -; fi
            ;;
        proc)
            local procs
            procs=$(list_procs)
            if [ -n "$procs" ]; then emit proc "$tag" "$(b64s "$procs")"; else emit proc "$tag" -; fi
            ;;
        # These take only REL, which lands in $2.
        watch) WATCH[$tag]=1 ;;
        unwatch) unset "WATCH[$tag]" ;;
        track) TRACK[$tag]=''; track_new=1 ;;
        untrack) unset "TRACK[$tag]" ;;
        quit) exit 0 ;;
    esac
}

scan_watches() {
    local rel
    for rel in "${!WATCH[@]}"; do
        if [ -s "$rel" ]; then
            unset "WATCH[$rel]"
            emit_file file "$rel"
        fi
    done
}

scan_tracks() {
    local rel sum
    for rel in "${!TRACK[@]}"; do
        if [ -f "$rel" ]; then sum=$(cksum < "$rel" 2>/dev/null); else sum=-; fi
        [ "$sum" = "${TRACK[$rel]}" ] && continue
        TRACK[$rel]=$sum
        if [ "$sum" = - ]; then emit track "$rel" -; else emit_file track "$rel"; fi
    done
}

# read -t times out with status > 128 and keeps any partial line in the
# variable, so a line that arrives in pieces is stitched back together here.
tick=0
track_new=0
partial=''
while :; do
    if IFS= read -r -t 0.1 line; then
        handle "$partial$line"
        partial=''
    else
        status=$?
        [ "$status" -gt 128 ] || exit 0
        partial+=$line
    fi
    scan_watches
    # Tracked files (the manifests) change rarely, so checksum them about once a
    # second, or straight away when a new track is registered.
    tick=$((tick + 1))
    if [ $((tick % 10)) -eq 0 ] || [ "$track_new" -eq 1 ]; then
        track_new=0
        scan_tracks
    fi
done
