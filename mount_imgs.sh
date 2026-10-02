#!/usr/bin/env bash
set -euo pipefail

IMG_DIR="${1:-/mnt/a/ReactOs/yggdrasilOS}"
MOUNT_BASE="/mnt/disks"
USER_UID=$(id -u)
USER_GID=$(id -g)

# Handle unmounting: ./mount_imgs.sh unmount
if [[ "${1:-}" == "unmount" || "${1:-}" == "umount" ]]; then
    echo "Unmounting all images in $MOUNT_BASE..."
    for mnt in "$MOUNT_BASE"/*; do
        [[ -d "$mnt" ]] || continue
        if mountpoint -q "$mnt"; then
            sudo umount "$mnt" && echo "Unmounted $mnt"
        fi
        sudo rmdir "$mnt" 2>/dev/null || true
    done

    # Detach loop devices pointing to *.img files
    losetup -a | grep '\.img)' | cut -d: -f1 | while read -r loopdev; do
        sudo losetup -d "$loopdev" && echo "Detached $loopdev"
    done
    exit 0
fi

sudo mkdir -p "$MOUNT_BASE"
shopt -s nullglob
images=("$IMG_DIR"/*.*.img)

if [[ ${#images[@]} -eq 0 ]]; then
    echo "No *.*.img files found in $IMG_DIR"
    exit 1
fi

for img in "${images[@]}"; do
    filename=$(basename "$img")
    base_name="${filename%.img}" # e.g., Fat.C, Fat.D, Fat.copy

    # Reuse existing loop device if already attached, otherwise attach new
    existing_loop=$(losetup -j "$img" | cut -d: -f1 | head -n1)
    if [[ -n "$existing_loop" ]]; then
        loopdev="$existing_loop"
        sudo partprobe "$loopdev" 2>/dev/null || true
    else
        loopdev=$(sudo losetup -fP --show "$img")
    fi
    echo "[$filename] -> $loopdev"

    # Find all partitions containing a valid filesystem (skips Non-FS data like Fat.C.img1)
    valid_parts=()
    for part in "${loopdev}"p*; do
        [[ -e "$part" ]] || continue
        fstype=$(sudo blkid -s TYPE -o value "$part" 2>/dev/null || true)
        if [[ -n "$fstype" ]]; then
            valid_parts+=("$part:$fstype")
        fi
    done

    if [[ ${#valid_parts[@]} -eq 0 ]]; then
        echo "  No mountable filesystem found in $filename, detaching $loopdev"
        sudo losetup -d "$loopdev"
        continue
    fi

    for entry in "${valid_parts[@]}"; do
        part="${entry%%:*}"
        fstype="${entry##*:}"
        part_num="${part##*p}"

        # Name folder after the image (e.g. Fat.C), append _pX only if multiple valid FS partitions exist
        if [[ ${#valid_parts[@]} -eq 1 ]]; then
            mnt_dir="$MOUNT_BASE/$base_name"
        else
            mnt_dir="$MOUNT_BASE/${base_name}_p${part_num}"
        fi

        sudo mkdir -p "$mnt_dir"

        if mountpoint -q "$mnt_dir"; then
            echo "  $part ($fstype) is already mounted at $mnt_dir"
            continue
        fi

        # Mount with user ownership so Windows Explorer can read/write freely
        case "$fstype" in
            vfat|fat|msdos|exfat|ntfs)
                sudo mount -o "uid=$USER_UID,gid=$USER_GID,umask=0022" "$part" "$mnt_dir"
                ;;
            *)
                sudo mount "$part" "$mnt_dir"
                ;;
        esac
        echo "  Mounted $part ($fstype) -> $mnt_dir"
    done
done

echo ""
echo "Opening mounted folders in Windows Explorer..."
/mnt/c/Windows/explorer.exe "$(wslpath -w "$MOUNT_BASE")" || true