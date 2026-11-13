#include <stdint.h>

volatile uint32_t roboworld_t00_compile_probe = 0x524F424Fu;

int main(void)
{
    return (int)(roboworld_t00_compile_probe & 0xFFu);
}
