#include "micro_wake.h"
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "sdkconfig.h"
#include <frontend.h>
#include <frontend_util.h>
#include <tensorflow/lite/micro/micro_interpreter.h>
#include <tensorflow/lite/micro/micro_mutable_op_resolver.h>
#include <tensorflow/lite/micro/micro_resource_variable.h>
#include <tensorflow/lite/schema/schema_generated.h>
#include <atomic>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <new>

extern const uint8_t model_begin[] asm("_binary_hey_marvin_tflite_start");
extern const uint8_t model_end[] asm("_binary_hey_marvin_tflite_end");
namespace {
FrontendState frontend{};
tflite::MicroMutableOpResolver<13> ops;
tflite::MicroInterpreter *engine;
TfLiteTensor *input,*output;
unsigned stride,step,window_index,warmup;
uint8_t scores[CONFIG_MARVIN_WAKE_WINDOW];
std::atomic<unsigned> calls{},max_us{},max_probability{},interval_probability{},faults{};
int64_t next_wake;
bool initialized;
}

esp_err_t marvin_micro_wake_open(void){
 if(initialized)return ESP_ERR_INVALID_STATE;
 flatbuffers::Verifier verifier(model_begin,model_end-model_begin);
 if(!tflite::VerifyModelBuffer(verifier))return ESP_ERR_INVALID_ARG;
 const tflite::Model *model=tflite::GetModel(model_begin);
 if(model->version()!=TFLITE_SCHEMA_VERSION)return ESP_ERR_NOT_SUPPORTED;
 #define REGISTER(name) if(ops.Add##name()!=kTfLiteOk)return ESP_FAIL
 REGISTER(CallOnce);REGISTER(VarHandle);REGISTER(Reshape);REGISTER(ReadVariable);
 REGISTER(StridedSlice);REGISTER(Concatenation);REGISTER(AssignVariable);REGISTER(Conv2D);
 REGISTER(FullyConnected);REGISTER(Logistic);
 REGISTER(Quantize);REGISTER(DepthwiseConv2D);
 REGISTER(SplitV);
 #undef REGISTER
 auto *arena=static_cast<uint8_t*>(heap_caps_aligned_alloc(16,65536,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT));
 auto *variables=static_cast<uint8_t*>(heap_caps_aligned_alloc(16,4096,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT));
 auto fail=[&](esp_err_t error){
  delete engine;engine=nullptr;input=output=nullptr;
  FrontendFreeStateContents(&frontend);memset(&frontend,0,sizeof(frontend));
  free(arena);free(variables);initialized=false;return error;
 };
 if(!arena||!variables)return fail(ESP_ERR_NO_MEM);
 auto *allocator=tflite::MicroAllocator::Create(variables,4096);
 auto *resources=allocator?tflite::MicroResourceVariables::Create(allocator,20):nullptr;
 if(!resources)return fail(ESP_ERR_NO_MEM);
 engine=new(std::nothrow) tflite::MicroInterpreter(model,ops,arena,65536,resources);
 if(!engine||engine->AllocateTensors()!=kTfLiteOk)return fail(ESP_ERR_NO_MEM);
 input=engine->input(0);output=engine->output(0);
 if(input->type!=kTfLiteInt8||input->dims->size!=3||input->dims->data[0]!=1||input->dims->data[2]!=40||input->dims->data[1]<1||input->dims->data[1]>16||output->type!=kTfLiteUInt8||output->bytes!=1||output->dims->size!=2||output->dims->data[0]!=1||output->dims->data[1]!=1)return fail(ESP_ERR_INVALID_SIZE);
 // The fixed feature scaling and unsigned score threshold require this quantization.
 if(input->params.zero_point!=-128||std::fabs(input->params.scale-26.f/255.f)>1e-6f||output->params.zero_point!=0||std::fabs(output->params.scale-1.f/256.f)>1e-7f)return fail(ESP_ERR_NOT_SUPPORTED);
 stride=input->dims->data[1];
 FrontendConfig cfg{};
 FrontendFillConfigWithDefaults(&cfg);
 cfg.window.size_ms=30;cfg.window.step_size_ms=10;
 cfg.filterbank.num_channels=40;cfg.filterbank.lower_band_limit=125;cfg.filterbank.upper_band_limit=7500;
 cfg.noise_reduction.smoothing_bits=10;cfg.noise_reduction.even_smoothing=.025f;cfg.noise_reduction.odd_smoothing=.06f;cfg.noise_reduction.min_signal_remaining=.05f;
 cfg.pcan_gain_control.enable_pcan=1;cfg.pcan_gain_control.strength=.95f;cfg.pcan_gain_control.offset=80;cfg.pcan_gain_control.gain_bits=21;
 cfg.log_scale.enable_log=1;cfg.log_scale.scale_shift=6;
 if(!FrontendPopulateState(&cfg,&frontend,16000))return fail(ESP_ERR_NO_MEM);
 initialized=true;marvin_micro_wake_reset();
 if(!initialized)return fail(ESP_FAIL);
 printf("{\"wakeEngine\":\"microWakeWord-evaluation\",\"wakePhrase\":\"Hey Marvin\",\"wakeStride\":%u}\n",stride);
 return ESP_OK;
}
void marvin_micro_wake_reset(void){
 if(!initialized)return;
 FrontendReset(&frontend);
 if(engine->Reset()!=kTfLiteOk){faults++;initialized=false;return;}
 memset(input->data.int8,0,input->bytes);
 memset(scores,0,sizeof(scores));step=window_index=0;warmup=(200+stride-1)/stride;next_wake=esp_timer_get_time()+2000000;
}
bool marvin_micro_wake_feed(const int16_t *pcm,size_t count,bool *detected){
 *detected=false;if(!initialized||!pcm)return false;
 while(count){
  size_t consumed=0;auto features=FrontendProcessSamples(&frontend,pcm,count,&consumed);
  if(!consumed||consumed>count){faults++;return false;}
  pcm+=consumed;count-=consumed;
  if(!features.size)continue;
  if(features.size!=40){faults++;return false;}
  for(unsigned i=0;i<40;i++){
   int value=(features.values[i]*256+333)/666-128;
   input->data.int8[step*40+i]=std::clamp(value,-128,127);
  }
  if(++step<stride)continue;
  step=0;
  int64_t start=esp_timer_get_time();auto status=engine->Invoke();unsigned elapsed=esp_timer_get_time()-start;
  calls++;if(elapsed>max_us)max_us=elapsed;
  if(status!=kTfLiteOk){faults++;return false;}
  unsigned probability=output->data.uint8[0];if(probability>max_probability)max_probability=probability;
  if(probability>interval_probability)interval_probability=probability;
  scores[window_index]=probability;window_index=(window_index+1)%CONFIG_MARVIN_WAKE_WINDOW;
  if(warmup){if(probability<CONFIG_MARVIN_WAKE_PROBABILITY_CUTOFF)warmup--;continue;}
  unsigned sum=0;for(auto score:scores)sum+=score;
  if(sum>=CONFIG_MARVIN_WAKE_PROBABILITY_CUTOFF*CONFIG_MARVIN_WAKE_WINDOW&&esp_timer_get_time()>=next_wake){
   *detected=true;next_wake=esp_timer_get_time()+2000000;warmup=(200+stride-1)/stride;memset(scores,0,sizeof(scores));
   printf("{\"wakeDetectedUs\":%lld,\"wakeProbability\":%u}\n",(long long)esp_timer_get_time(),sum/CONFIG_MARVIN_WAKE_WINDOW);
  }
 }
 return true;
}
void marvin_micro_wake_status(void){
 printf("{\"wakeRuntime\":{\"phrase\":\"Hey Marvin\",\"model\":\"microWakeWord-evaluation\",\"calls\":%u,\"maxInferenceUs\":%u,\"maxProbability\":%u,\"intervalMaxProbability\":%u,\"cutoff\":%u,\"faults\":%u}}\n",calls.load(),max_us.load(),max_probability.load(),interval_probability.exchange(0),CONFIG_MARVIN_WAKE_PROBABILITY_CUTOFF,faults.load());
}
