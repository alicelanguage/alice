;; Alice thin runtime for runtime = "alice" (ADR 0034): allocator, mark-sweep GC, memory, strings, numbers, I/O.
;; Hand-written wasm text: assembled into every module by Backend/Wasm/WatReader.cs (only the functions a program
;; reaches are kept) and valid standalone for wabt (`wat2wasm rt.wat`). The parts that are plain Alice live in
;; rt.al. Imports from "alice" are functions the compiler generates (they are bound, not imported); the
;; wasi_snapshot_preview1 imports go through Wasi.cs.
;;
;; Memory: [0, 1024) null page | static data (literals, vtables, statics) | runtime area | shadow stack | heap.
;; Every heap object starts with an 8-byte header: word0 = type id (bits 0-23, never 0 for a live object) | mark
;; (bit 24) | noscan (bit 25); word1 = length for strings and arrays. Payload at +8. Pointers are 8-byte aligned.
;; The collector is non-moving and finds roots conservatively in the shadow stack and the statics: any aligned word
;; that is the address of a live object keeps it. Objects without pointers (strings, number arrays) are noscan.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  ;; compiler-generated: raise the runtime error `code` as an Alice exception (or abort under --panic=abort)
  (import "alice" "throw_code" (func $throw_code (param i32)))

  (memory 2)

  ;; set by the compiler
  (global $rt (mut i32) (i32.const 0))            ;; runtime area (see the offsets below)
  (global $statics_start (mut i32) (i32.const 0))
  (global $statics_end (mut i32) (i32.const 0))
  (global $stack_base (mut i32) (i32.const 0))
  (global $stack_top (mut i32) (i32.const 0))
  (global $sp (mut i32) (i32.const 0))
  (global $heap_start (mut i32) (i32.const 0))
  ;; state
  (global $heap_end (mut i32) (i32.const 0))
  (global $since_gc (mut i32) (i32.const 0))
  (global $gc_threshold (mut i32) (i32.const 4194304))
  (global $live (mut i32) (i32.const 0))
  (global $mark_overflow (mut i32) (i32.const 0))
  (global $mark_top (mut i32) (i32.const 0))
  (global $page_hint (mut i32) (i32.const 0))
  (global $out_len (mut i32) (i32.const 0))
  (global $err_len (mut i32) (i32.const 0))
  (global $gc_count (mut i32) (i32.const 0))
  (global $total_alloc (mut i64) (i64.const 0))

  ;; runtime area offsets: pagemap 0 (65536 bytes, one per 64 KiB page: 0 free/not heap, 1..18 small class,
  ;; 254 large head, 255 large continuation) | free heads 65536 | bump 65664 | bump end 65792 | class sizes 65920 |
  ;; iov 66048 | stdout buffer 66064 (4096) | stderr buffer 70160 (1024) | mark stack 71184 (65536) |
  ;; arena slabs 136720 (8 x 32: widths 1..7, entry 0 the region marks) | end 136976

  ;; ------------------------------------------------------------------ init

  (func $rt_init
    (local $c i32) (local $s i32)
    ;; class sizes: 16 32 48 64 96 128 192 256 384 512 768 1024 1536 2048 3072 4096 6144 8192
    (local.set $c (i32.const 1))
    (local.set $s (i32.const 16))
    (block $done
      (loop $next
        (i32.store offset=65920 (i32.add (global.get $rt) (i32.shl (local.get $c) (i32.const 2))) (local.get $s))
        (br_if $done (i32.ge_u (local.get $c) (i32.const 18)))
        (if (i32.lt_u (local.get $c) (i32.const 4))
          (then (local.set $s (i32.add (local.get $s) (i32.const 16))))
          (else
            ;; alternate x1.5 and x1.333: 64 96 128 192 ...
            (if (i32.and (local.get $c) (i32.const 1))
              (then (local.set $s (i32.add (local.get $s) (i32.shr_u (local.get $s) (i32.const 1)))))
              (else (local.set $s (i32.add (local.get $s) (i32.div_u (local.get $s) (i32.const 3))))))))
        (local.set $c (i32.add (local.get $c) (i32.const 1)))
        (br $next)))
    (global.set $heap_end (i32.shl (memory.size) (i32.const 16)))
    (global.set $page_hint (i32.shr_u (global.get $heap_start) (i32.const 16))))

  (func $class_size (param $c i32) (result i32)
    (i32.load offset=65920 (i32.add (global.get $rt) (i32.shl (local.get $c) (i32.const 2)))))

  ;; smallest class holding `size` bytes, 0 when it needs a large object
  (func $size_class (param $size i32) (result i32)
    (local $c i32)
    (if (i32.gt_u (local.get $size) (i32.const 8192)) (then (return (i32.const 0))))
    (if (i32.le_u (local.get $size) (i32.const 64))
      (then (return (i32.shr_u (i32.add (local.get $size) (i32.const 15)) (i32.const 4)))))
    (local.set $c (i32.const 5))
    (block $found
      (loop $next
        (br_if $found (i32.le_u (local.get $size) (call $class_size (local.get $c))))
        (local.set $c (i32.add (local.get $c) (i32.const 1)))
        (br $next)))
    (local.get $c))

  ;; ------------------------------------------------------------------ pages

  (func $pagemap_get (param $p i32) (result i32)
    (i32.load8_u (i32.add (global.get $rt) (local.get $p))))

  (func $pagemap_set (param $p i32) (param $v i32)
    (i32.store8 (i32.add (global.get $rt) (local.get $p)) (local.get $v)))

  ;; `n` contiguous free pages (zeroed), growing memory when the heap has no such run; returns the first page
  (func $take_pages (param $n i32) (result i32)
    (local $p i32) (local $run i32) (local $end i32) (local $start i32) (local $grow i32)
    (local.set $end (i32.shr_u (global.get $heap_end) (i32.const 16)))
    (local.set $p (global.get $page_hint))
    (local.set $start (local.get $p))
    (block $none
      (loop $scan
        (br_if $none (i32.ge_u (local.get $p) (local.get $end)))
        (if (call $pagemap_get (local.get $p))
          (then (local.set $run (i32.const 0)) (local.set $start (i32.add (local.get $p) (i32.const 1))))
          (else
            (local.set $run (i32.add (local.get $run) (i32.const 1)))
            (if (i32.eq (local.get $run) (local.get $n))
              (then
                (if (i32.eq (local.get $n) (i32.const 1)) (then (global.set $page_hint (local.get $start))))
                (return (local.get $start))))))
        (local.set $p (i32.add (local.get $p) (i32.const 1)))
        (br $scan)))
    ;; a free tail run [start, end) is extended by growing
    (local.set $grow (i32.sub (local.get $n) (local.get $run)))
    (if (i32.lt_s (memory.grow (local.get $grow)) (i32.const 0))
      (then (call $throw_code (i32.const 9)) (unreachable)))
    (global.set $heap_end (i32.shl (memory.size) (i32.const 16)))
    (local.get $start))

  ;; ------------------------------------------------------------------ allocation

  ;; a zeroed object of `size` bytes (header included) with header word0 = `type`; collects first when due
  (func $alloc (param $size i32) (param $type i32) (result i32)
    (local $c i32) (local $p i32) (local $at i32) (local $n i32) (local $page i32)
    (local.set $size (i32.and (i32.add (local.get $size) (i32.const 7)) (i32.const -8)))
    (global.set $since_gc (i32.add (global.get $since_gc) (local.get $size)))
    (global.set $total_alloc (i64.add (global.get $total_alloc) (i64.extend_i32_u (local.get $size))))
    (if (i32.gt_u (global.get $since_gc) (global.get $gc_threshold)) (then (call $gc_collect)))
    (local.set $c (call $size_class (local.get $size)))
    (if (i32.eqz (local.get $c))
      (then
        ;; large: whole pages, the object at page start + 8 (the first word holds the page count)
        (local.set $n (i32.shr_u (i32.add (local.get $size) (i32.const 65543)) (i32.const 16)))
        (local.set $page (call $take_pages (local.get $n)))
        (call $pagemap_set (local.get $page) (i32.const 254))
        (local.set $at (i32.const 1))
        (block $done
          (loop $cont
            (br_if $done (i32.ge_u (local.get $at) (local.get $n)))
            (call $pagemap_set (i32.add (local.get $page) (local.get $at)) (i32.const 255))
            (local.set $at (i32.add (local.get $at) (i32.const 1)))
            (br $cont)))
        (local.set $p (i32.shl (local.get $page) (i32.const 16)))
        (i32.store (local.get $p) (local.get $n))
        (local.set $p (i32.add (local.get $p) (i32.const 8)))
        (i32.store (local.get $p) (local.get $type))
        (return (local.get $p))))
    (local.set $at (i32.add (global.get $rt) (i32.shl (local.get $c) (i32.const 2))))
    ;; free list
    (local.set $p (i32.load offset=65536 (local.get $at)))
    (if (local.get $p)
      (then
        (i32.store offset=65536 (local.get $at) (i32.load offset=4 (local.get $p)))
        (i32.store offset=4 (local.get $p) (i32.const 0))
        (i32.store (local.get $p) (local.get $type))
        (return (local.get $p))))
    ;; bump within the class's current page
    (local.set $p (i32.load offset=65664 (local.get $at)))
    (local.set $size (call $class_size (local.get $c)))
    (if (i32.gt_u (i32.add (local.get $p) (local.get $size)) (i32.load offset=65792 (local.get $at)))
      (then
        (local.set $page (call $take_pages (i32.const 1)))
        (call $pagemap_set (local.get $page) (local.get $c))
        (local.set $p (i32.shl (local.get $page) (i32.const 16)))
        (i32.store offset=65792 (local.get $at) (i32.add (local.get $p) (i32.const 65536)))))
    (i32.store offset=65664 (local.get $at) (i32.add (local.get $p) (local.get $size)))
    (i32.store (local.get $p) (local.get $type))
    (local.get $p))

  ;; explicit free of an owned value (`release`): back to its free list or its pages; zeroed
  (func $rt_free (param $p i32)
    (local $page i32) (local $c i32) (local $n i32) (local $at i32) (local $size i32)
    (if (i32.eqz (call $is_object (local.get $p))) (then (return)))
    (local.set $page (i32.shr_u (local.get $p) (i32.const 16)))
    (local.set $c (call $pagemap_get (local.get $page)))
    (if (i32.eq (local.get $c) (i32.const 254))
      (then
        (local.set $n (i32.load (i32.sub (local.get $p) (i32.const 8))))
        (memory.fill (i32.shl (local.get $page) (i32.const 16)) (i32.const 0) (i32.shl (local.get $n) (i32.const 16)))
        (local.set $at (i32.const 0))
        (block $done
          (loop $next
            (br_if $done (i32.ge_u (local.get $at) (local.get $n)))
            (call $pagemap_set (i32.add (local.get $page) (local.get $at)) (i32.const 0))
            (local.set $at (i32.add (local.get $at) (i32.const 1)))
            (br $next)))
        (if (i32.lt_u (local.get $page) (global.get $page_hint)) (then (global.set $page_hint (local.get $page))))
        (return)))
    (local.set $size (call $class_size (local.get $c)))
    (memory.fill (local.get $p) (i32.const 0) (local.get $size))
    (local.set $at (i32.add (global.get $rt) (i32.shl (local.get $c) (i32.const 2))))
    (i32.store offset=4 (local.get $p) (i32.load offset=65536 (local.get $at)))
    (i32.store offset=65536 (local.get $at) (local.get $p)))

  ;; whether `p` is the address of a live heap object
  (func $is_object (param $p i32) (result i32)
    (local $page i32) (local $c i32)
    (if (i32.or (i32.lt_u (local.get $p) (global.get $heap_start)) (i32.ge_u (local.get $p) (global.get $heap_end)))
      (then (return (i32.const 0))))
    (if (i32.and (local.get $p) (i32.const 7)) (then (return (i32.const 0))))
    (local.set $page (i32.shr_u (local.get $p) (i32.const 16)))
    (local.set $c (call $pagemap_get (local.get $page)))
    (if (i32.eqz (local.get $c)) (then (return (i32.const 0))))
    (if (i32.eq (local.get $c) (i32.const 254))
      (then (return (i32.and (i32.eq (i32.and (local.get $p) (i32.const 65535)) (i32.const 8))
                             (i32.ne (i32.load (local.get $p)) (i32.const 0))))))
    (if (i32.eq (local.get $c) (i32.const 255)) (then (return (i32.const 0))))
    (if (i32.rem_u (i32.and (local.get $p) (i32.const 65535)) (call $class_size (local.get $c)))
      (then (return (i32.const 0))))
    (i32.ne (i32.load (local.get $p)) (i32.const 0)))

  ;; payload end of an object (its slot or pages)
  (func $object_end (param $p i32) (result i32)
    (local $c i32)
    (local.set $c (call $pagemap_get (i32.shr_u (local.get $p) (i32.const 16))))
    (if (result i32) (i32.eq (local.get $c) (i32.const 254))
      (then (i32.add (i32.sub (local.get $p) (i32.const 8)) (i32.shl (i32.load (i32.sub (local.get $p) (i32.const 8))) (i32.const 16))))
      (else (i32.add (local.get $p) (call $class_size (local.get $c))))))

  ;; ------------------------------------------------------------------ collector

  (func $mark_value (param $v i32)
    (local $w i32)
    (if (i32.eqz (call $is_object (local.get $v))) (then (return)))
    (local.set $w (i32.load (local.get $v)))
    (if (i32.and (local.get $w) (i32.const 0x1000000)) (then (return)))
    (i32.store (local.get $v) (i32.or (local.get $w) (i32.const 0x1000000)))
    (if (i32.and (local.get $w) (i32.const 0x2000000)) (then (return)))
    (if (i32.ge_u (global.get $mark_top) (i32.const 65536))
      (then (global.set $mark_overflow (i32.const 1)) (return)))
    (i32.store offset=71184 (i32.add (global.get $rt) (global.get $mark_top)) (local.get $v))
    (global.set $mark_top (i32.add (global.get $mark_top) (i32.const 4))))

  (func $mark_range (param $from i32) (param $to i32)
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $from) (local.get $to)))
        (call $mark_value (i32.load (local.get $from)))
        (local.set $from (i32.add (local.get $from) (i32.const 4)))
        (br $next))))

  (func $mark_drain
    (local $p i32)
    (block $done
      (loop $next
        (br_if $done (i32.eqz (global.get $mark_top)))
        (global.set $mark_top (i32.sub (global.get $mark_top) (i32.const 4)))
        (local.set $p (i32.load offset=71184 (i32.add (global.get $rt) (global.get $mark_top))))
        (call $mark_range (i32.add (local.get $p) (i32.const 8)) (call $object_end (local.get $p)))
        (br $next))))

  ;; calls `f` on every live object: mode 0 = rescan marked objects (mark stack overflow), 1 = sweep
  (func $heap_walk (param $mode i32)
    (local $page i32) (local $end i32) (local $c i32) (local $p i32) (local $lim i32) (local $size i32) (local $w i32) (local $n i32)
    (local.set $page (i32.shr_u (global.get $heap_start) (i32.const 16)))
    (local.set $end (i32.shr_u (global.get $heap_end) (i32.const 16)))
    (block $done
      (loop $pages
        (br_if $done (i32.ge_u (local.get $page) (local.get $end)))
        (local.set $c (call $pagemap_get (local.get $page)))
        (local.set $n (i32.const 1))
        (if (i32.eq (local.get $c) (i32.const 254))
          (then
            (local.set $p (i32.add (i32.shl (local.get $page) (i32.const 16)) (i32.const 8)))
            (local.set $n (i32.load (i32.sub (local.get $p) (i32.const 8))))
            (call $walk_one (local.get $mode) (local.get $p)))
          (else
            (if (i32.and (i32.ne (local.get $c) (i32.const 0)) (i32.lt_u (local.get $c) (i32.const 254)))
              (then
                (local.set $size (call $class_size (local.get $c)))
                (local.set $p (i32.shl (local.get $page) (i32.const 16)))
                (local.set $lim (i32.sub (i32.add (local.get $p) (i32.const 65536)) (local.get $size)))
                (block $slots_done
                  (loop $slots
                    (br_if $slots_done (i32.gt_u (local.get $p) (local.get $lim)))
                    (if (i32.load (local.get $p)) (then (call $walk_one (local.get $mode) (local.get $p))))
                    (local.set $p (i32.add (local.get $p) (local.get $size)))
                    (br $slots)))))))
        (local.set $page (i32.add (local.get $page) (local.get $n)))
        (br $pages))))

  (func $walk_one (param $mode i32) (param $p i32)
    (local $w i32)
    (local.set $w (i32.load (local.get $p)))
    (if (i32.eqz (local.get $mode))
      (then
        (if (i32.eq (i32.and (local.get $w) (i32.const 0x3000000)) (i32.const 0x1000000))
          (then (call $mark_range (i32.add (local.get $p) (i32.const 8)) (call $object_end (local.get $p))) (call $mark_drain)))
        (return)))
    (if (i32.and (local.get $w) (i32.const 0x1000000))
      (then
        (i32.store (local.get $p) (i32.and (local.get $w) (i32.const -16777217)))
        (global.set $live (i32.add (global.get $live) (i32.sub (call $object_end (local.get $p)) (local.get $p)))))
      (else (call $rt_free (local.get $p)))))

  (func $gc_collect
    (global.set $gc_count (i32.add (global.get $gc_count) (i32.const 1)))
    (global.set $mark_top (i32.const 0))
    (global.set $mark_overflow (i32.const 0))
    (call $mark_range (global.get $sp) (global.get $stack_top))
    (call $mark_range (global.get $statics_start) (global.get $statics_end))
    (call $mark_range (i32.add (global.get $rt) (i32.const 136720)) (i32.add (global.get $rt) (i32.const 136976)))
    (call $mark_drain)
    (block $stable
      (loop $again
        (br_if $stable (i32.eqz (global.get $mark_overflow)))
        (global.set $mark_overflow (i32.const 0))
        (call $heap_walk (i32.const 0))
        (br $again)))
    (global.set $live (i32.const 0))
    (call $heap_walk (i32.const 1))
    (global.set $since_gc (i32.const 0))
    ;; next collection after the heap doubles (at least 4 MiB of allocation)
    (global.set $gc_threshold (select (global.get $live) (i32.const 4194304) (i32.gt_u (global.get $live) (i32.const 4194304)))))

  (func $gc_count_get (result i32) (global.get $gc_count))
  (func $total_allocated (result i64) (global.get $total_alloc))

  ;; ------------------------------------------------------------------ affine arena (ADR 0037)
  ;; One slab per slot width w (1..7 words of 8 bytes), shared by every arena enum of that width, one lane (lane 0).
  ;; Slab state at rt+136720+32w: +0 data (a noscan Array(UInt64): slot i at data+8+8wi, grown by doubling, so a
  ;; slot index survives growth), +4 len (bump cursor), +8 capacity (slots), +12 free-list head (index+1, 0 none;
  ;; the first word of a free slot holds the next head). Entry 0: +0 marks (Array(Int32), 14 words per open region:
  ;; len and free of every width), +4 depth, +8 capacity (regions). The collector scans this table as roots.

  (func $arena_state (param $w i32) (result i32)
    (i32.add (i32.add (global.get $rt) (i32.const 136720)) (i32.shl (local.get $w) (i32.const 5))))

  (func $arena_slot (param $w i32) (param $i i32) (result i32)
    (i32.add (i32.add (i32.load (call $arena_state (local.get $w))) (i32.const 8))
      (i32.shl (i32.mul (local.get $i) (local.get $w)) (i32.const 3))))

  (func $arena_grow (param $s i32) (param $w i32)
    (local $cap i32) (local $new i32) (local $old i32)
    (local.set $cap (i32.load offset=8 (local.get $s)))
    (local.set $cap (select (i32.shl (local.get $cap) (i32.const 1)) (i32.const 1024) (local.get $cap)))
    (local.set $new (call $array_new (i32.mul (local.get $cap) (local.get $w)) (i32.const 8) (i32.const 2)))
    (local.set $old (i32.load (local.get $s)))
    (if (local.get $old)
      (then
        (memory.copy (i32.add (local.get $new) (i32.const 8)) (i32.add (local.get $old) (i32.const 8))
          (i32.shl (i32.mul (i32.load offset=4 (local.get $s)) (local.get $w)) (i32.const 3)))))
    (i32.store (local.get $s) (local.get $new))
    (i32.store offset=8 (local.get $s) (local.get $cap)))

  ;; a slot index: the free list first, else the bump cursor (the caller stores every word)
  (func $arena_alloc (param $w i32) (result i32)
    (local $s i32) (local $i i32) (local $f i32)
    (local.set $s (call $arena_state (local.get $w)))
    (local.set $f (i32.load offset=12 (local.get $s)))
    (if (local.get $f)
      (then
        (local.set $i (i32.sub (local.get $f) (i32.const 1)))
        (i32.store offset=12 (local.get $s) (i32.load (call $arena_slot (local.get $w) (local.get $i))))
        (return (local.get $i))))
    (local.set $i (i32.load offset=4 (local.get $s)))
    (if (i32.ge_u (local.get $i) (i32.load offset=8 (local.get $s))) (then (call $arena_grow (local.get $s) (local.get $w))))
    (i32.store offset=4 (local.get $s) (i32.add (local.get $i) (i32.const 1)))
    (local.get $i))

  (func $arena_free (param $w i32) (param $i i32)
    (local $s i32)
    (local.set $s (call $arena_state (local.get $w)))
    (i64.store (call $arena_slot (local.get $w) (local.get $i)) (i64.extend_i32_u (i32.load offset=12 (local.get $s))))
    (i32.store offset=12 (local.get $s) (i32.add (local.get $i) (i32.const 1))))

  ;; Take / Borrow of a w-word slot: copies it to dst, then (frees) pushes it on the free list
  (func $arena_read (param $w i32) (param $i i32) (param $dst i32) (param $frees i32)
    (memory.copy (local.get $dst) (call $arena_slot (local.get $w) (local.get $i)) (i32.shl (local.get $w) (i32.const 3)))
    (if (local.get $frees) (then (call $arena_free (local.get $w) (local.get $i)))))

  (func $arena_read1 (param $i i32) (param $frees i32) (result i64)
    (local $v i64)
    (local.set $v (i64.load (call $arena_slot (i32.const 1) (local.get $i))))
    (if (local.get $frees) (then (call $arena_free (i32.const 1) (local.get $i))))
    (local.get $v))

  ;; Drop: frees the slot of a term (a packed term is left alone). Words in mask that hold slot terms are dropped too,
  ;; their subtrees with all; the last child is the next turn of the loop (no recursion along a spine).
  (func $arena_drop (param $w i32) (param $t i64) (param $mask i32) (param $all i32)
    (local $p i32) (local $k i32) (local $last i32) (local $v i64)
    (loop $tail
      (if (i32.and (i32.wrap_i64 (i64.shr_u (local.get $t) (i64.const 32))) (i32.const -2130706433)) (then (return)))
      (local.set $p (call $arena_slot (local.get $w) (i32.wrap_i64 (local.get $t))))
      (local.set $last (i32.const -1))
      (local.set $k (i32.const 0))
      (block $done
        (loop $words
          (br_if $done (i32.ge_u (local.get $k) (local.get $w)))
          (local.set $v (i64.load (i32.add (local.get $p) (i32.shl (local.get $k) (i32.const 3)))))
          (if (i32.and (i32.ne (i32.and (local.get $mask) (i32.shl (i32.const 1) (local.get $k))) (i32.const 0)) (i64.ge_s (local.get $v) (i64.const 0)))
            (then
              (if (i32.ge_s (local.get $last) (i32.const 0))
                (then (call $arena_drop (local.get $w) (i64.load (i32.add (local.get $p) (i32.shl (local.get $last) (i32.const 3)))) (local.get $all) (local.get $all))))
              (local.set $last (local.get $k))))
          (local.set $k (i32.add (local.get $k) (i32.const 1)))
          (br $words)))
      (if (i32.ge_s (local.get $last) (i32.const 0))
        (then (local.set $v (i64.load (i32.add (local.get $p) (i32.shl (local.get $last) (i32.const 3)))))))
      (call $arena_free (local.get $w) (i32.wrap_i64 (local.get $t)))
      (if (i32.lt_s (local.get $last) (i32.const 0)) (then (return)))
      (local.set $t (local.get $v))
      (local.set $mask (local.get $all))
      (br $tail)))

  ;; scope ... end: the cursor and free list of every width are saved, the free list starts empty; the end restores them
  (func $arena_region_enter (result i32)
    (local $m i32) (local $d i32) (local $cap i32) (local $new i32) (local $e i32) (local $w i32) (local $s i32)
    (local.set $m (call $arena_state (i32.const 0)))
    (local.set $d (i32.load offset=4 (local.get $m)))
    (if (i32.ge_u (local.get $d) (i32.load offset=8 (local.get $m)))
      (then
        (local.set $cap (select (i32.shl (local.get $d) (i32.const 1)) (i32.const 16) (local.get $d)))
        (local.set $new (call $array_new (i32.mul (local.get $cap) (i32.const 14)) (i32.const 4) (i32.const 2)))
        (if (local.get $d)
          (then (memory.copy (i32.add (local.get $new) (i32.const 8)) (i32.add (i32.load (local.get $m)) (i32.const 8)) (i32.mul (local.get $d) (i32.const 56)))))
        (i32.store (local.get $m) (local.get $new))
        (i32.store offset=8 (local.get $m) (local.get $cap))))
    (local.set $e (i32.add (i32.add (i32.load (local.get $m)) (i32.const 8)) (i32.mul (local.get $d) (i32.const 56))))
    (local.set $w (i32.const 1))
    (block $done
      (loop $each
        (br_if $done (i32.gt_u (local.get $w) (i32.const 7)))
        (local.set $s (call $arena_state (local.get $w)))
        (i32.store (local.get $e) (i32.load offset=4 (local.get $s)))
        (i32.store offset=4 (local.get $e) (i32.load offset=12 (local.get $s)))
        (i32.store offset=12 (local.get $s) (i32.const 0))
        (local.set $e (i32.add (local.get $e) (i32.const 8)))
        (local.set $w (i32.add (local.get $w) (i32.const 1)))
        (br $each)))
    (i32.store offset=4 (local.get $m) (i32.add (local.get $d) (i32.const 1)))
    (i32.add (local.get $d) (i32.const 1)))

  (func $arena_region_end (param $r i32)
    (local $m i32) (local $d i32) (local $e i32) (local $w i32) (local $s i32)
    (if (i32.eqz (local.get $r)) (then (return)))
    (local.set $m (call $arena_state (i32.const 0)))
    (local.set $d (i32.sub (local.get $r) (i32.const 1)))
    (local.set $e (i32.add (i32.add (i32.load (local.get $m)) (i32.const 8)) (i32.mul (local.get $d) (i32.const 56))))
    (local.set $w (i32.const 1))
    (block $done
      (loop $each
        (br_if $done (i32.gt_u (local.get $w) (i32.const 7)))
        (local.set $s (call $arena_state (local.get $w)))
        (i32.store offset=4 (local.get $s) (i32.load (local.get $e)))
        (i32.store offset=12 (local.get $s) (i32.load offset=4 (local.get $e)))
        (local.set $e (i32.add (local.get $e) (i32.const 8)))
        (local.set $w (i32.add (local.get $w) (i32.const 1)))
        (br $each)))
    (i32.store offset=4 (local.get $m) (local.get $d)))

  ;; ------------------------------------------------------------------ shadow stack

  ;; a frame of `n` bytes (zeroed: stale pointers must not keep garbage alive); returns its base
  (func $frame_enter (param $n i32) (result i32)
    (local $sp i32)
    (local.set $sp (i32.sub (global.get $sp) (local.get $n)))
    (if (i32.lt_u (local.get $sp) (global.get $stack_base)) (then (call $throw_code (i32.const 8)) (unreachable)))
    (global.set $sp (local.get $sp))
    (memory.fill (local.get $sp) (i32.const 0) (local.get $n))
    (local.get $sp))

  ;; ------------------------------------------------------------------ memory helpers

  (func $memcpy (param $dst i32) (param $src i32) (param $n i32)
    (memory.copy (local.get $dst) (local.get $src) (local.get $n)))

  (func $memset (param $dst i32) (param $v i32) (param $n i32)
    (memory.fill (local.get $dst) (local.get $v) (local.get $n)))

  ;; an array of `len` elements of `elem` bytes: header type `type` (2 number array | 3 reference array | 4 struct array)
  (func $array_new (param $len i32) (param $elem i32) (param $type i32) (result i32)
    (local $p i32) (local $bytes i64)
    (if (i32.lt_s (local.get $len) (i32.const 0)) (then (call $throw_code (i32.const 6)) (unreachable)))
    (local.set $bytes (i64.add (i64.mul (i64.extend_i32_u (local.get $len)) (i64.extend_i32_u (local.get $elem))) (i64.const 8)))
    (if (i64.gt_u (local.get $bytes) (i64.const 0x7FFF0000)) (then (call $throw_code (i32.const 9)) (unreachable)))
    (local.set $p (call $alloc (i32.wrap_i64 (local.get $bytes))
      (select (i32.or (local.get $type) (i32.const 0x2000000)) (local.get $type) (i32.eq (local.get $type) (i32.const 2)))))
    (i32.store offset=4 (local.get $p) (local.get $len))
    (local.get $p))

  ;; ------------------------------------------------------------------ strings (UTF-8; header type 1, noscan)

  (func $str_new (param $len i32) (result i32)
    (local $p i32)
    (local.set $p (call $alloc (i32.add (local.get $len) (i32.const 8)) (i32.const 0x2000001)))
    (i32.store offset=4 (local.get $p) (local.get $len))
    (local.get $p))

  (func $str_len (param $s i32) (result i32)
    (if (result i32) (local.get $s) (then (i32.load offset=4 (local.get $s))) (else (i32.const 0))))

  (func $str_concat (param $a i32) (param $b i32) (result i32)
    (local $la i32) (local $lb i32) (local $r i32)
    (local.set $la (call $str_len (local.get $a)))
    (local.set $lb (call $str_len (local.get $b)))
    (if (i32.eqz (local.get $lb)) (then (if (local.get $a) (then (return (local.get $a))))))
    (if (i32.eqz (local.get $la)) (then (if (local.get $b) (then (return (local.get $b))))))
    (local.set $r (call $str_new (i32.add (local.get $la) (local.get $lb))))
    (memory.copy (i32.add (local.get $r) (i32.const 8)) (i32.add (local.get $a) (i32.const 8)) (local.get $la))
    (memory.copy (i32.add (i32.add (local.get $r) (i32.const 8)) (local.get $la)) (i32.add (local.get $b) (i32.const 8)) (local.get $lb))
    (local.get $r))

  (func $str_eq (param $a i32) (param $b i32) (result i32)
    (local $n i32) (local $i i32)
    (if (i32.eq (local.get $a) (local.get $b)) (then (return (i32.const 1))))
    (if (i32.or (i32.eqz (local.get $a)) (i32.eqz (local.get $b))) (then (return (i32.const 0))))
    (local.set $n (i32.load offset=4 (local.get $a)))
    (if (i32.ne (local.get $n) (i32.load offset=4 (local.get $b))) (then (return (i32.const 0))))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (if (i32.ne (i32.load8_u offset=8 (i32.add (local.get $a) (local.get $i))) (i32.load8_u offset=8 (i32.add (local.get $b) (local.get $i))))
          (then (return (i32.const 0))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (i32.const 1))

  ;; ordinal comparison: <0, 0, >0
  (func $str_cmp (param $a i32) (param $b i32) (result i32)
    (local $la i32) (local $lb i32) (local $n i32) (local $i i32) (local $d i32)
    (local.set $la (call $str_len (local.get $a)))
    (local.set $lb (call $str_len (local.get $b)))
    (local.set $n (select (local.get $la) (local.get $lb) (i32.lt_u (local.get $la) (local.get $lb))))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (local.set $d (i32.sub (i32.load8_u offset=8 (i32.add (local.get $a) (local.get $i))) (i32.load8_u offset=8 (i32.add (local.get $b) (local.get $i)))))
        (if (local.get $d) (then (return (local.get $d))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (i32.sub (local.get $la) (local.get $lb)))

  ;; FNV-1a
  (func $str_hash (param $s i32) (result i32)
    (local $h i32) (local $i i32) (local $n i32)
    (local.set $h (i32.const -2128831035))
    (local.set $n (call $str_len (local.get $s)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (local.set $h (i32.mul (i32.xor (local.get $h) (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i)))) (i32.const 16777619)))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (local.get $h))

  ;; s[i] as a Char (a byte: strings are UTF-8)
  (func $str_at (param $s i32) (param $i i32) (result i32)
    (if (i32.ge_u (local.get $i) (call $str_len (local.get $s))) (then (call $throw_code (i32.const 1)) (unreachable)))
    (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i))))

  (func $str_sub (param $s i32) (param $from i32) (param $n i32) (result i32)
    (local $r i32)
    (if (i32.or (i32.lt_s (local.get $from) (i32.const 0)) (i32.or (i32.lt_s (local.get $n) (i32.const 0))
          (i32.gt_u (i32.add (local.get $from) (local.get $n)) (call $str_len (local.get $s)))))
      (then (call $throw_code (i32.const 6)) (unreachable)))
    (local.set $r (call $str_new (local.get $n)))
    (memory.copy (i32.add (local.get $r) (i32.const 8)) (i32.add (i32.add (local.get $s) (i32.const 8)) (local.get $from)) (local.get $n))
    (local.get $r))

  ;; ASCII case mapping: `up` 1 = upcase, 0 = downcase
  (func $str_case (param $s i32) (param $up i32) (result i32)
    (local $n i32) (local $r i32) (local $i i32) (local $c i32)
    (local.set $n (call $str_len (local.get $s)))
    (local.set $r (call $str_new (local.get $n)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (local.set $c (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i))))
        (if (local.get $up)
          (then (if (i32.lt_u (i32.sub (local.get $c) (i32.const 97)) (i32.const 26)) (then (local.set $c (i32.sub (local.get $c) (i32.const 32))))))
          (else (if (i32.lt_u (i32.sub (local.get $c) (i32.const 65)) (i32.const 26)) (then (local.set $c (i32.add (local.get $c) (i32.const 32)))))))
        (i32.store8 offset=8 (i32.add (local.get $r) (local.get $i)) (local.get $c))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (local.get $r))

  ;; a Char (a Unicode scalar value) as a UTF-8 string
  (func $str_from_char (param $c i32) (result i32)
    (local $r i32)
    (if (i32.lt_u (local.get $c) (i32.const 0x80))
      (then
        (local.set $r (call $str_new (i32.const 1)))
        (i32.store8 offset=8 (local.get $r) (local.get $c))
        (return (local.get $r))))
    (if (i32.lt_u (local.get $c) (i32.const 0x800))
      (then
        (local.set $r (call $str_new (i32.const 2)))
        (i32.store8 offset=8 (local.get $r) (i32.or (i32.const 0xC0) (i32.shr_u (local.get $c) (i32.const 6))))
        (i32.store8 offset=9 (local.get $r) (i32.or (i32.const 0x80) (i32.and (local.get $c) (i32.const 63))))
        (return (local.get $r))))
    (local.set $r (call $str_new (i32.const 3)))
    (i32.store8 offset=8 (local.get $r) (i32.or (i32.const 0xE0) (i32.shr_u (local.get $c) (i32.const 12))))
    (i32.store8 offset=9 (local.get $r) (i32.or (i32.const 0x80) (i32.and (i32.shr_u (local.get $c) (i32.const 6)) (i32.const 63))))
    (i32.store8 offset=10 (local.get $r) (i32.or (i32.const 0x80) (i32.and (local.get $c) (i32.const 63))))
    (local.get $r))

  ;; ------------------------------------------------------------------ number formatting

  ;; unsigned 64-bit to decimal, with a leading '-' when `neg`
  (func $u64_to_str (param $v i64) (param $neg i32) (result i32)
    (local $buf i32) (local $i i32) (local $r i32) (local $n i32)
    ;; digits right to left in a 24-byte frame
    (local.set $buf (call $frame_enter (i32.const 24)))
    (local.set $i (i32.const 24))
    (loop $digit
      (local.set $i (i32.sub (local.get $i) (i32.const 1)))
      (i32.store8 (i32.add (local.get $buf) (local.get $i)) (i32.add (i32.const 48) (i32.wrap_i64 (i64.rem_u (local.get $v) (i64.const 10)))))
      (local.set $v (i64.div_u (local.get $v) (i64.const 10)))
      (br_if $digit (i64.ne (local.get $v) (i64.const 0))))
    (if (local.get $neg)
      (then
        (local.set $i (i32.sub (local.get $i) (i32.const 1)))
        (i32.store8 (i32.add (local.get $buf) (local.get $i)) (i32.const 45))))
    (local.set $n (i32.sub (i32.const 24) (local.get $i)))
    (local.set $r (call $str_new (local.get $n)))
    (memory.copy (i32.add (local.get $r) (i32.const 8)) (i32.add (local.get $buf) (local.get $i)) (local.get $n))
    (global.set $sp (i32.add (local.get $buf) (i32.const 24)))
    (local.get $r))

  (func $i64_to_str (param $v i64) (result i32)
    (if (result i32) (i64.lt_s (local.get $v) (i64.const 0))
      (then (call $u64_to_str (i64.sub (i64.const 0) (local.get $v)) (i32.const 1)))
      (else (call $u64_to_str (local.get $v) (i32.const 0)))))

  (func $i32_to_str (param $v i32) (result i32)
    (call $i64_to_str (i64.extend_i32_s (local.get $v))))

  (func $u32_to_str (param $v i32) (result i32)
    (call $u64_to_str (i64.extend_i32_u (local.get $v)) (i32.const 0)))

  (func $bool_to_str (param $b i32) (result i32)
    (local $r i32)
    (if (local.get $b)
      (then
        (local.set $r (call $str_new (i32.const 4)))
        (i32.store offset=8 (local.get $r) (i32.const 0x65757274)))
      (else
        (local.set $r (call $str_new (i32.const 5)))
        (i32.store offset=8 (local.get $r) (i32.const 0x736c6166))
        (i32.store8 offset=12 (local.get $r) (i32.const 0x65))))
    (local.get $r))

  ;; ------------------------------------------------------------------ number parsing (String#to_i, Int32.Parse)

  ;; trimmed decimal to i64; code 5 (FormatException) when it is not a number; `bits` 32 checks the Int32 range
  (func $parse_i64 (param $s i32) (param $bits i32) (result i64)
    (local $i i32) (local $n i32) (local $c i32) (local $neg i32) (local $v i64) (local $any i32)
    (local.set $n (call $str_len (local.get $s)))
    ;; trim ASCII white space
    (block $l (loop $lt
      (br_if $l (i32.ge_u (local.get $i) (local.get $n)))
      (local.set $c (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i))))
      (br_if $l (i32.and (i32.ne (local.get $c) (i32.const 32)) (i32.gt_u (i32.sub (local.get $c) (i32.const 9)) (i32.const 4))))
      (local.set $i (i32.add (local.get $i) (i32.const 1)))
      (br $lt)))
    (block $r (loop $rt
      (br_if $r (i32.le_u (local.get $n) (local.get $i)))
      (local.set $c (i32.load8_u offset=7 (i32.add (local.get $s) (local.get $n))))
      (br_if $r (i32.and (i32.ne (local.get $c) (i32.const 32)) (i32.gt_u (i32.sub (local.get $c) (i32.const 9)) (i32.const 4))))
      (local.set $n (i32.sub (local.get $n) (i32.const 1)))
      (br $rt)))
    (if (i32.lt_u (local.get $i) (local.get $n))
      (then
        (local.set $c (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i))))
        (if (i32.or (i32.eq (local.get $c) (i32.const 45)) (i32.eq (local.get $c) (i32.const 43)))
          (then
            (local.set $neg (i32.eq (local.get $c) (i32.const 45)))
            (local.set $i (i32.add (local.get $i) (i32.const 1)))))))
    (block $done (loop $next
      (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
      (local.set $c (i32.sub (i32.load8_u offset=8 (i32.add (local.get $s) (local.get $i))) (i32.const 48)))
      (if (i32.gt_u (local.get $c) (i32.const 9)) (then (call $throw_code (i32.const 5)) (unreachable)))
      (if (i64.gt_u (local.get $v) (i64.const 922337203685477579)) (then (call $throw_code (i32.const 2)) (unreachable)))
      (local.set $v (i64.add (i64.mul (local.get $v) (i64.const 10)) (i64.extend_i32_u (local.get $c))))
      (if (i64.lt_s (local.get $v) (i64.const 0))
        (then (if (i32.eqz (i32.and (local.get $neg) (i64.eq (local.get $v) (i64.const 0x8000000000000000))))
          (then (call $throw_code (i32.const 2)) (unreachable)))))
      (local.set $any (i32.const 1))
      (local.set $i (i32.add (local.get $i) (i32.const 1)))
      (br $next)))
    (if (i32.eqz (local.get $any)) (then (call $throw_code (i32.const 5)) (unreachable)))
    (if (local.get $neg) (then (local.set $v (i64.sub (i64.const 0) (local.get $v)))))
    (if (i32.eq (local.get $bits) (i32.const 32))
      (then (if (i64.ne (local.get $v) (i64.extend_i32_s (i32.wrap_i64 (local.get $v)))) (then (call $throw_code (i32.const 2)) (unreachable)))))
    (local.get $v))

  ;; ------------------------------------------------------------------ checked arithmetic helpers

  (func $mul_ovf_i64 (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    (local.set $r (i64.mul (local.get $a) (local.get $b)))
    (if (i64.ne (local.get $a) (i64.const 0))
      (then
        (if (i32.or
              (i32.and (i64.eq (local.get $a) (i64.const -1)) (i64.eq (local.get $b) (i64.const 0x8000000000000000)))
              (i64.ne (i64.div_s (local.get $r) (local.get $a)) (local.get $b)))
          (then (call $throw_code (i32.const 2)) (unreachable)))))
    (local.get $r))

  (func $mul_ovf_u64 (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    (local.set $r (i64.mul (local.get $a) (local.get $b)))
    (if (i64.ne (local.get $a) (i64.const 0))
      (then (if (i64.ne (i64.div_u (local.get $r) (local.get $a)) (local.get $b)) (then (call $throw_code (i32.const 2)) (unreachable)))))
    (local.get $r))

  ;; ------------------------------------------------------------------ output (buffered fd_write)

  (func $flush_fd (param $fd i32) (param $buf i32) (param $len i32)
    (local $iov i32)
    (if (i32.eqz (local.get $len)) (then (return)))
    (local.set $iov (i32.add (global.get $rt) (i32.const 66048)))
    (i32.store (local.get $iov) (local.get $buf))
    (i32.store offset=4 (local.get $iov) (local.get $len))
    (drop (call $fd_write (local.get $fd) (local.get $iov) (i32.const 1) (i32.add (local.get $iov) (i32.const 8)))))

  (func $out_flush
    (call $flush_fd (i32.const 1) (i32.add (global.get $rt) (i32.const 66064)) (global.get $out_len))
    (global.set $out_len (i32.const 0))
    (call $flush_fd (i32.const 2) (i32.add (global.get $rt) (i32.const 70160)) (global.get $err_len))
    (global.set $err_len (i32.const 0)))

  ;; `n` bytes at `p` to stdout (fd 1) or stderr (fd 2)
  (func $write_bytes (param $fd i32) (param $p i32) (param $n i32)
    (local $cap i32) (local $len i32) (local $buf i32)
    (if (i32.eq (local.get $fd) (i32.const 2))
      (then (local.set $cap (i32.const 1024)) (local.set $len (global.get $err_len)) (local.set $buf (i32.add (global.get $rt) (i32.const 70160))))
      (else (local.set $cap (i32.const 4096)) (local.set $len (global.get $out_len)) (local.set $buf (i32.add (global.get $rt) (i32.const 66064)))))
    (if (i32.gt_u (i32.add (local.get $len) (local.get $n)) (local.get $cap))
      (then
        (call $out_flush)
        (local.set $len (i32.const 0))
        (if (i32.gt_u (local.get $n) (local.get $cap))
          (then (call $flush_fd (local.get $fd) (local.get $p) (local.get $n)) (return)))))
    (memory.copy (i32.add (local.get $buf) (local.get $len)) (local.get $p) (local.get $n))
    (local.set $len (i32.add (local.get $len) (local.get $n)))
    (if (i32.eq (local.get $fd) (i32.const 2)) (then (global.set $err_len (local.get $len))) (else (global.set $out_len (local.get $len)))))

  (func $write_str (param $fd i32) (param $s i32)
    (if (local.get $s) (then (call $write_bytes (local.get $fd) (i32.add (local.get $s) (i32.const 8)) (i32.load offset=4 (local.get $s))))))

  (func $write_nl (param $fd i32)
    (local $p i32)
    (local.set $p (call $frame_enter (i32.const 8)))
    (i32.store8 (local.get $p) (i32.const 10))
    (call $write_bytes (local.get $fd) (local.get $p) (i32.const 1))
    (global.set $sp (i32.add (local.get $p) (i32.const 8))))

  ;; puts(String): the line and a newline
  (func $puts (param $s i32)
    (call $write_str (i32.const 1) (local.get $s))
    (call $write_nl (i32.const 1)))

  (func $print (param $s i32)
    (call $write_str (i32.const 1) (local.get $s)))

  (func $eputs (param $s i32)
    (call $write_str (i32.const 2) (local.get $s))
    (call $write_nl (i32.const 2)))

  (func $exit (param $code i32)
    (call $out_flush)
    (call $proc_exit (local.get $code)))
)
