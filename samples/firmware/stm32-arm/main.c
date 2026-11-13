#include <stdint.h>
#include <stddef.h>

#ifndef FW_BUILD_ID
#define FW_BUILD_ID "unbuilt"
#endif

#define REG32(address) (*(volatile uint32_t *)(address))

#define RCC_AHB1ENR REG32(0x40023830u)
#define RCC_APB1ENR REG32(0x40023840u)
#define GPIOA_MODER REG32(0x40020000u)
#define GPIOA_IDR   REG32(0x40020010u)
#define GPIOA_AFRL  REG32(0x40020020u)
#define GPIOD_MODER REG32(0x40020C00u)
#define GPIOD_AFRL  REG32(0x40020C20u)
#define GPIOD_BSRR  REG32(0x40020C18u)

#define TIM3_BASE 0x40000400u
#define TIM3_CR1   REG32(TIM3_BASE + 0x00u)
#define TIM3_EGR   REG32(TIM3_BASE + 0x14u)
#define TIM3_CCMR1 REG32(TIM3_BASE + 0x18u)
#define TIM3_CCER  REG32(TIM3_BASE + 0x20u)
#define TIM3_PSC   REG32(TIM3_BASE + 0x28u)
#define TIM3_ARR   REG32(TIM3_BASE + 0x2Cu)
#define TIM3_CCR1  REG32(TIM3_BASE + 0x34u)
#define TIM3_CCR2  REG32(TIM3_BASE + 0x38u)

#define TIM2_BASE 0x40000000u
#define TIM2_CR1  REG32(TIM2_BASE + 0x00u)
#define TIM2_EGR  REG32(TIM2_BASE + 0x14u)
#define TIM2_PSC  REG32(TIM2_BASE + 0x28u)
#define TIM2_ARR  REG32(TIM2_BASE + 0x2Cu)
#define TIM2_CNT  REG32(TIM2_BASE + 0x24u)

#define UART4_BASE 0x40004C00u
#define UART4_SR   REG32(UART4_BASE + 0x00u)
#define UART4_DR   REG32(UART4_BASE + 0x04u)
#define UART4_BRR  REG32(UART4_BASE + 0x08u)
#define UART4_CR1  REG32(UART4_BASE + 0x0Cu)

#define UART_SR_RXNE (1u << 5)
#define UART_SR_TXE  (1u << 7)
#define UART_CR1_RE  (1u << 2)
#define UART_CR1_TE  (1u << 3)
#define UART_CR1_UE  (1u << 13)
#define LINE_CAPACITY 48u
#define RX_POLL_LIMIT 2000000u

static uint32_t echoStartedUs;
static uint32_t lastDistanceMm;
static int echoLevel;
static int distanceValid;

static void poll_distance_echo(void)
{
    int currentLevel = (GPIOA_IDR & 1u) != 0u;
    if (currentLevel && !echoLevel) {
        echoStartedUs = TIM2_CNT;
    } else if (!currentLevel && echoLevel) {
        uint32_t durationUs = TIM2_CNT - echoStartedUs;
        if (durationUs > 0u && durationUs <= 25000u) {
            lastDistanceMm = (durationUs * 343u) / 2000u;
            distanceValid = 1;
        } else {
            distanceValid = 0;
        }
    }
    echoLevel = currentLevel;
}

static void uart_write_byte(char byte)
{
    uint32_t timeout = RX_POLL_LIMIT;
    while ((UART4_SR & UART_SR_TXE) == 0u && timeout > 0u) {
        poll_distance_echo();
        --timeout;
    }
    UART4_DR = (uint32_t)(uint8_t)byte;
}

static void uart_write(const char *text)
{
    while (*text != '\0') {
        uart_write_byte(*text++);
    }
}

static void uart_write_uint(uint32_t value)
{
    char digits[10];
    uint32_t count = 0u;
    do {
        digits[count++] = (char)('0' + (value % 10u));
        value /= 10u;
    } while (value != 0u && count < sizeof(digits));
    while (count > 0u) uart_write_byte(digits[--count]);
}

static int uart_read_byte(uint8_t *byte)
{
    uint32_t timeout = RX_POLL_LIMIT;
    while ((UART4_SR & UART_SR_RXNE) == 0u && timeout > 0u) {
        poll_distance_echo();
        --timeout;
    }
    if (timeout == 0u) {
        return 0;
    }
    *byte = (uint8_t)UART4_DR;
    return 1;
}

static void uart_init(void)
{
    /* STM32F407 reset clock is 16 MHz HSI; UART4 is on APB1. */
    RCC_AHB1ENR |= (1u << 3);
    RCC_AHB1ENR |= (1u << 0);
    RCC_APB1ENR |= (1u << 19);
    RCC_APB1ENR |= (1u << 1);
    RCC_APB1ENR |= (1u << 0);
    (void)RCC_AHB1ENR;
    (void)RCC_APB1ENR;

    /* Discovery board UART4 is exposed by the Renode model; map TX/RX on PD0/PD1 AF8. */
    GPIOD_MODER = (GPIOD_MODER & ~0xFu) | 0xAu;
    GPIOD_AFRL = (GPIOD_AFRL & ~0xFFu) | 0x88u;

    /* TIM3_CH1/CH2 on PA6/PA7 AF2; PD2/PD3 are motor direction pins. */
    GPIOD_MODER = (GPIOD_MODER & ~(0xFu << 4)) | (0x5u << 4);
    GPIOA_MODER = (GPIOA_MODER & ~(0xFu << 12)) | (0xAu << 12);
    GPIOA_AFRL = (GPIOA_AFRL & 0x0000FFFFu) | 0x22000000u;
    GPIOA_MODER &= ~0x3u; /* PA0: ultrasonic echo input (STM32F407 GPIO). */

    /* TIM2 is a free-running 1 MHz firmware timebase for echo pulse width. */
    TIM2_CR1 = 0u;
    TIM2_PSC = 9u; /* Renode STM32F4 APB1 timer clock is 10 MHz; prescale to 1 MHz. */
    TIM2_ARR = 0xFFFFFFFFu;
    TIM2_EGR = 1u;
    TIM2_CR1 = 1u;

    /* TIM3 clock is 10 MHz in the pinned Renode STM32F4 model. */
    TIM3_CR1 = 0u;
    TIM3_PSC = 9u;
    TIM3_ARR = 999u;
    TIM3_CCMR1 = 0x6060u; /* PWM mode 1 on channels 1 and 2. */
    TIM3_CCR1 = 0u;
    TIM3_CCR2 = 0u;
    TIM3_EGR = 1u;
    TIM3_CCER = (1u << 0) | (1u << 4);
    TIM3_CR1 = (1u << 7) | 1u; /* auto-reload preload, counter enable */

    UART4_CR1 = 0u;
    UART4_BRR = 139u; /* 16 MHz / 115200, oversampling by 16. */
    UART4_CR1 = UART_CR1_UE | UART_CR1_TE | UART_CR1_RE;
}

static int line_equals(const char *line, const char *expected)
{
    size_t index = 0u;
    while (line[index] != '\0' && expected[index] != '\0') {
        if (line[index] != expected[index]) {
            return 0;
        }
        ++index;
    }
    return line[index] == '\0' && expected[index] == '\0';
}

static int parse_int(const char **cursor, int *value)
{
    const char *p = *cursor;
    int sign = 1;
    int result = 0;
    int digits = 0;
    if (*p == '-') { sign = -1; ++p; }
    else if (*p == '+') { ++p; }
    while (*p >= '0' && *p <= '9') {
        result = result * 10 + (*p - '0');
        if (result > 1000) return 0;
        ++p;
        ++digits;
    }
    if (digits == 0) return 0;
    *value = sign * result;
    *cursor = p;
    return 1;
}

static int parse_motor_values(const char *line, int *left, int *right)
{
    const char *p = line;
    if (p[0] != 'M' || p[1] != 'O' || p[2] != 'T' || p[3] != 'O' || p[4] != 'R' || p[5] != ' ') return 0;
    p += 6;
    if (!parse_int(&p, left) || *p++ != ' ' || !parse_int(&p, right) || *p != '\0') return 0;
    return 1;
}

static void apply_motor_command(int left, int right)
{
    uint32_t setBits = 0u;
    uint32_t resetBits = 0u;
    if (left >= 0) setBits |= 1u << 2; else resetBits |= 1u << 2;
    if (right >= 0) setBits |= 1u << 3; else resetBits |= 1u << 3;
    GPIOD_BSRR = setBits | (resetBits << 16);
    TIM3_CCR1 = (uint32_t)(left < 0 ? -left : left) * 10u;
    TIM3_CCR2 = 0u; /* Single-axis arm: firmware controls channel 1 only. */
}

/* Kept non-static with debug symbols for the Renode GDB breakpoint acceptance check. */
void handle_command(const char *line)
{
    if (line_equals(line, "PING")) {
        uart_write("PONG ");
        uart_write(FW_BUILD_ID);
        uart_write("\n");
    } else if (line_equals(line, "STATUS?")) {
        uart_write("STATUS MCU=OK UART=OK\n");
    } else if (line_equals(line, "SENSOR?")) {
        poll_distance_echo();
        uart_write("DIST mm=");
        uart_write_uint(distanceValid ? lastDistanceMm : 0u);
        uart_write(distanceValid ? " valid=1\n" : " valid=0\n");
    } else {
        int left = 0;
        int right = 0;
        if (parse_motor_values(line, &left, &right)) {
            if (left < -100 || left > 100 || right < -100 || right > 100) {
                uart_write("ERR RANGE\n");
            } else {
                apply_motor_command(left, right);
                uart_write("OK MOTOR\n");
            }
        } else {
            uart_write("ERR CMD\n");
        }
    }
}

int main(void)
{
    uart_init();
    uart_write("BOOT ");
    uart_write(FW_BUILD_ID);
    uart_write("\n");

    char line[LINE_CAPACITY];
    size_t length = 0u;
    int overflow = 0;
    for (;;) {
        uint8_t byte = 0u;
        if (!uart_read_byte(&byte)) {
            if (length > 0u || overflow) {
                length = 0u;
                overflow = 0;
                uart_write("ERR TIMEOUT\n");
            }
            continue;
        }

        if (byte == '\r') {
            continue;
        }
        if (byte == '\n') {
            if (overflow) {
                uart_write("ERR LINE\n");
            } else {
                line[length] = '\0';
                poll_distance_echo();
                handle_command(line);
            }
            length = 0u;
            overflow = 0;
            continue;
        }

        if (length + 1u < sizeof(line)) {
            line[length++] = (char)byte;
        } else {
            overflow = 1;
        }
    }
}

