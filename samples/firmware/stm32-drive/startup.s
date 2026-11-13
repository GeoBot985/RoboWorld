.syntax unified
.cpu cortex-m4
.thumb

.global vector_table
.global Reset_Handler
.type Reset_Handler, %function

.section .isr_vector,"a",%progbits
vector_table:
    .word __StackTop
    .word Reset_Handler
    .word Default_Handler
    .word Default_Handler
    .word Default_Handler
    .word Default_Handler
    .word Default_Handler
    .word 0
    .word 0
    .word 0
    .word 0
    .word Default_Handler
    .word Default_Handler
    .word 0
    .word Default_Handler
    .word Default_Handler
    .rept 82
    .word Default_Handler
    .endr

.section .text.Reset_Handler,"ax",%progbits
.thumb_func
Reset_Handler:
    ldr r0, =__sidata
    ldr r1, =__sdata
    ldr r2, =__edata
1:
    cmp r1, r2
    bcs 2f
    ldr r3, [r0], #4
    str r3, [r1], #4
    b 1b
2:
    ldr r1, =__sbss
    ldr r2, =__ebss
    movs r3, #0
3:
    cmp r1, r2
    bcs 4f
    str r3, [r1], #4
    b 3b
4:
    bl main
5:
    b 5b

.section .text.Default_Handler,"ax",%progbits
.thumb_func
Default_Handler:
    b Default_Handler
