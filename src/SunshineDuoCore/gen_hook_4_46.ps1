# Build the GameViewer 4.46 hook. The 4.0 generator and binary remain unchanged.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$tmp = Join-Path $root '.temp'
if (-not (Test-Path -LiteralPath $tmp)) { throw "Missing temporary directory: $tmp" }
$asm = Join-Path $tmp 'gv_hook_4_46.s'
$obj = Join-Path $tmp 'gv_hook_4_46.o'
$dll = Join-Path $tmp 'gv_hook_4_46.dll'
$as = 'C:\Komandare\Unixlike\bin\as.exe'
$ld = 'C:\Komandare\Unixlike\bin\ld.exe'
$source = @'
    .text
    .globl DllMain
DllMain:
    sub $0x28, %rsp
    cmp $1, %edx
    jne .Ldm_ret
    call install
.Ldm_ret:
    mov $1, %eax
    add $0x28, %rsp
    ret

# The original WTS W-entry prologue ends on an instruction boundary
# after 16 bytes on this host. Recheck it after a Windows update.
install:
    push %rbx
    push %rsi
    push %rdi
    push %r12
    push %r13
    sub $0x20, %rsp
    lea wtsname(%rip), %rcx
    call GetModuleHandleA
    test %rax, %rax
    jnz .Lgot_wts
    lea wtsname(%rip), %rcx
    call LoadLibraryA
    test %rax, %rax
    jz .Linstall_end
.Lgot_wts:
    mov %rax, %rbx
    mov %rbx, %rcx
    lea nW(%rip), %rdx
    call GetProcAddress
    test %rax, %rax
    jz .Linstall_end
    mov %rax, realW(%rip)
    # Refuse to patch an unknown wtsapi32 prologue.
    movabs $0x41575620245c8948, %r10
    cmp %r10, (%rax)
    jne .Linstall_end
    movabs $0xf6334530ec834856, %r10
    cmp %r10, 8(%rax)
    jne .Linstall_end
    cld

    # Build W trampoline: 16 complete prologue bytes, then absolute jump.
    xor %ecx, %ecx
    mov $64, %edx
    mov $0x3000, %r8d
    mov $0x40, %r9d
    call VirtualAlloc
    test %rax, %rax
    jz .Linstall_end
    mov %rax, trampW(%rip)
    mov %rax, %rdi
    mov realW(%rip), %rsi
    mov $16, %ecx
    rep movsb
    mov realW(%rip), %rax
    add $16, %rax
    movw $0xB848, (%rdi)
    movq %rax, 2(%rdi)
    movw $0xE0FF, 10(%rdi)

    # The 4.46 service imports only WTSEnumerateSessionsW.
    # Publish the event only after its patch is installed.
    mov realW(%rip), %rcx
    mov $14, %edx
    mov $0x40, %r8d
    lea oldprot(%rip), %r9
    call VirtualProtect
    test %eax, %eax
    jz .Linstall_end
    mov realW(%rip), %rbx
    movw $0x25FF, (%rbx)
    movl $0, 2(%rbx)
    lea hookW(%rip), %rax
    movq %rax, 6(%rbx)
    mov %rbx, %rcx
    mov $14, %edx
    mov oldprot(%rip), %r8d
    lea oldprot(%rip), %r9
    call VirtualProtect
    mov $-1, %rcx
    mov %rbx, %rdx
    mov $14, %r8d
    call FlushInstructionCache

    xor %ecx, %ecx
    mov $1, %edx
    mov $1, %r8d
    lea evname(%rip), %r9
    call CreateEventA
.Linstall_end:
    add $0x20, %rsp
    pop %r13
    pop %r12
    pop %rdi
    pop %rsi
    pop %rbx
    ret

# On entry RSP points at the caller return address; [RSP+0x28] is the
# 5th argument (count pointer). Seven preserved registers make the stack
# 16-byte aligned before CALL, with 0x20 shadow + the 5th argument slot.
# Each invocation owns its saved state, including reentrant/concurrent calls.
hookW:
    push %rbx
    push %rsi
    push %rdi
    push %r12
    push %r13
    push %r14
    push %r15
    sub $0x30, %rsp
    mov %r9, %r12
    mov 0x90(%rsp), %r13
    mov %r13, 0x20(%rsp)
    call *trampW(%rip)
    mov %eax, %r14d
    test %eax, %eax
    jz .Lw_done
    test %r12, %r12
    jz .Lw_done
    test %r13, %r13
    jz .Lw_done
    mov (%r12), %rbx
    mov (%r13), %r13d
    test %rbx, %rbx
    jz .Lw_done
    call WTSGetActiveConsoleSessionId
    mov %eax, %r15d
    xor %esi, %esi
.Lw_loop:
    cmp %r13d, %esi
    jae .Lw_done
    lea (%rsi,%rsi,2), %rdi
    mov (%rbx,%rdi,8), %eax
    cmpl $0, 16(%rbx,%rdi,8)
    jne .Lw_next
    cmp %r15d, %eax
    je .Lw_next
    movl $4, 16(%rbx,%rdi,8)
.Lw_next:
    inc %esi
    jmp .Lw_loop
.Lw_done:
    mov %r14d, %eax
    add $0x30, %rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rdi
    pop %rsi
    pop %rbx
    ret

    .data
    .align 8
realW:   .quad 0
trampW:  .quad 0
oldprot: .quad 0
wtsname: .asciz "wtsapi32.dll"
nW:      .asciz "WTSEnumerateSessionsW"
evname:  .asciz "Global\\gv_hook_injected"
'@
[IO.File]::WriteAllText($asm, $source + "`n", (New-Object Text.ASCIIEncoding))
& $as -o $obj $asm
if ($LASTEXITCODE -ne 0) { throw "as failed: $LASTEXITCODE" }
& $ld -m i386pep --dll -e DllMain --export-all-symbols -o $dll $obj 'C:\Windows\System32\kernel32.dll'
if ($LASTEXITCODE -ne 0) { throw "ld failed: $LASTEXITCODE" }
Write-Output "Built $dll"
Get-Item -LiteralPath $dll | Select-Object Name,Length
