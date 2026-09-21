#pragma once
void vTaskDelay(unsigned ticks);
int xTaskCreate(void (*task)(void*),const char *name,unsigned stack,void *arg,unsigned priority,void *handle);
