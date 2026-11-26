/*
 * NV-KeepAlive v1.0.0 (Linux) - Minimalist, Zero-Dependency NVIDIA GPU KeepAlive
 * Backends (compiled into a single binary via dlopen, zero SDK headers required):
 *   1) Vulkan Compute (libvulkan.so.1 + embedded SPIR-V 1.0) - ~5-20 MiB VRAM
 *   2) CUDA Driver API (libcuda.so.1 + embedded PTX sm_50+)  - ~200-300 MiB VRAM
 * Supports per-GPU backend assignment (--targets 0:vulkan,1:cuda), PCI Bus ID sorting,
 * NVML external-load yield, and clean exit when no NVIDIA GPU is present.
 *
 * Build:
 *   gcc -O2 -Wall -Wextra -pthread nv_keepalive.c -ldl -o nv-keepalive
 */
#define _POSIX_C_SOURCE 200809L

#include <ctype.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#define NVK_VERSION "1.1.2"

/* ===================== CUDA Driver API types ===================== */
typedef int CUresult;
typedef int CUdevice;
typedef void *CUcontext;
typedef void *CUmodule;
typedef void *CUfunction;
typedef void *CUstream;
typedef uint64_t CUdeviceptr;

typedef CUresult (*fn_cuInit)(unsigned int);
typedef CUresult (*fn_cuDeviceGetCount)(int *);
typedef CUresult (*fn_cuDeviceGet)(CUdevice *, int);
typedef CUresult (*fn_cuDeviceGetName)(char *, int, CUdevice);
typedef CUresult (*fn_cuDeviceGetPCIBusId)(char *, int, CUdevice);
typedef CUresult (*fn_cuDeviceTotalMem_v2)(size_t *, CUdevice);
typedef CUresult (*fn_cuCtxCreate_v2)(CUcontext *, unsigned int, CUdevice);
typedef CUresult (*fn_cuCtxDestroy_v2)(CUcontext);
typedef CUresult (*fn_cuCtxSynchronize)(void);
typedef CUresult (*fn_cuModuleLoadData)(CUmodule *, const void *);
typedef CUresult (*fn_cuModuleUnload)(CUmodule);
typedef CUresult (*fn_cuModuleGetFunction)(CUfunction *, CUmodule, const char *);
typedef CUresult (*fn_cuMemAlloc_v2)(CUdeviceptr *, size_t);
typedef CUresult (*fn_cuMemFree_v2)(CUdeviceptr);
typedef CUresult (*fn_cuLaunchKernel)(
    CUfunction,
    unsigned int, unsigned int, unsigned int,
    unsigned int, unsigned int, unsigned int,
    unsigned int, CUstream, void **, void **);

/* ===================== Vulkan 1.0/1.1 minimal ABI types ===================== */
typedef int32_t  VkResult;
typedef uint32_t VkFlags;
typedef uint64_t VkDeviceSize;
typedef void    *VkInstance;
typedef void    *VkPhysicalDevice;
typedef void    *VkDevice;
typedef void    *VkQueue;
typedef void    *VkCommandBuffer;
typedef uint64_t VkBuffer;
typedef uint64_t VkDeviceMemory;
typedef uint64_t VkDescriptorSetLayout;
typedef uint64_t VkPipelineLayout;
typedef uint64_t VkShaderModule;
typedef uint64_t VkPipeline;
typedef uint64_t VkPipelineCache;
typedef uint64_t VkDescriptorPool;
typedef uint64_t VkDescriptorSet;
typedef uint64_t VkCommandPool;
typedef uint64_t VkFence;

typedef struct { uint32_t sType; const void *pNext; const char *pApplicationName; uint32_t applicationVersion; const char *pEngineName; uint32_t engineVersion; uint32_t apiVersion; } VkApplicationInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; const VkApplicationInfo *pApplicationInfo; uint32_t enabledLayerCount; const char *const *ppEnabledLayerNames; uint32_t enabledExtensionCount; const char *const *ppEnabledExtensionNames; } VkInstanceCreateInfo;
typedef struct { uint32_t queueFlags; uint32_t queueCount; uint32_t timestampValidBits; uint32_t minW, minH, minD; } VkQueueFamilyProperties;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t queueFamilyIndex; uint32_t queueCount; const float *pQueuePriorities; } VkDeviceQueueCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t queueCreateInfoCount; const VkDeviceQueueCreateInfo *pQueueCreateInfos; uint32_t enabledLayerCount; const char *const *ppEnabledLayerNames; uint32_t enabledExtensionCount; const char *const *ppEnabledExtensionNames; const void *pEnabledFeatures; } VkDeviceCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; VkDeviceSize size; VkFlags usage; uint32_t sharingMode; uint32_t queueFamilyIndexCount; const uint32_t *pQueueFamilyIndices; } VkBufferCreateInfo;
typedef struct { VkDeviceSize size; VkDeviceSize alignment; uint32_t memoryTypeBits; } VkMemoryRequirements;
typedef struct { uint32_t sType; const void *pNext; VkDeviceSize allocationSize; uint32_t memoryTypeIndex; } VkMemoryAllocateInfo;
typedef struct { uint32_t binding; uint32_t descriptorType; uint32_t descriptorCount; VkFlags stageFlags; const void *pImmutableSamplers; } VkDescriptorSetLayoutBinding;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t bindingCount; const VkDescriptorSetLayoutBinding *pBindings; } VkDescriptorSetLayoutCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t setLayoutCount; const VkDescriptorSetLayout *pSetLayouts; uint32_t pushConstantRangeCount; const void *pPushConstantRanges; } VkPipelineLayoutCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; size_t codeSize; const uint32_t *pCode; } VkShaderModuleCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t stage; VkShaderModule module; const char *pName; const void *pSpecializationInfo; } VkPipelineShaderStageCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; VkPipelineShaderStageCreateInfo stage; VkPipelineLayout layout; VkPipeline basePipelineHandle; int32_t basePipelineIndex; } VkComputePipelineCreateInfo;
typedef struct { uint32_t type; uint32_t descriptorCount; } VkDescriptorPoolSize;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t maxSets; uint32_t poolSizeCount; const VkDescriptorPoolSize *pPoolSizes; } VkDescriptorPoolCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkDescriptorPool descriptorPool; uint32_t descriptorSetCount; const VkDescriptorSetLayout *pSetLayouts; } VkDescriptorSetAllocateInfo;
typedef struct { VkBuffer buffer; VkDeviceSize offset; VkDeviceSize range; } VkDescriptorBufferInfo;
typedef struct { uint32_t sType; const void *pNext; VkDescriptorSet dstSet; uint32_t dstBinding; uint32_t dstArrayElement; uint32_t descriptorCount; uint32_t descriptorType; const void *pImageInfo; const VkDescriptorBufferInfo *pBufferInfo; const void *pTexelBufferView; } VkWriteDescriptorSet;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; uint32_t queueFamilyIndex; } VkCommandPoolCreateInfo;
typedef struct { uint32_t sType; const void *pNext; VkCommandPool commandPool; uint32_t level; uint32_t commandBufferCount; } VkCommandBufferAllocateInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; const void *pInheritanceInfo; } VkCommandBufferBeginInfo;
typedef struct { uint32_t sType; const void *pNext; uint32_t waitSemaphoreCount; const void *pWaitSemaphores; const void *pWaitDstStageMask; uint32_t commandBufferCount; const VkCommandBuffer *pCommandBuffers; uint32_t signalSemaphoreCount; const void *pSignalSemaphores; } VkSubmitInfo;
typedef struct { uint32_t sType; const void *pNext; VkFlags flags; } VkFenceCreateInfo;
typedef struct { uint32_t sType; void *pNext; uint32_t pciDomain; uint32_t pciBus; uint32_t pciDevice; uint32_t pciFunction; } VkPhysicalDevicePCIBusInfoPropertiesEXT;

typedef VkResult (*fn_vkCreateInstance)(const VkInstanceCreateInfo *, const void *, VkInstance *);
typedef void     (*fn_vkDestroyInstance)(VkInstance, const void *);
typedef void    *(*fn_vkGetInstanceProcAddr)(VkInstance, const char *);
typedef void     (*fn_vkGetPhysicalDeviceProperties2)(VkPhysicalDevice, void *);
typedef VkResult (*fn_vkEnumeratePhysicalDevices)(VkInstance, uint32_t *, VkPhysicalDevice *);
typedef void     (*fn_vkGetPhysicalDeviceProperties)(VkPhysicalDevice, void *);
typedef void     (*fn_vkGetPhysicalDeviceMemoryProperties)(VkPhysicalDevice, void *);
typedef void     (*fn_vkGetPhysicalDeviceQueueFamilyProperties)(VkPhysicalDevice, uint32_t *, VkQueueFamilyProperties *);
typedef VkResult (*fn_vkCreateDevice)(VkPhysicalDevice, const VkDeviceCreateInfo *, const void *, VkDevice *);
typedef void     (*fn_vkDestroyDevice)(VkDevice, const void *);
typedef void     (*fn_vkGetDeviceQueue)(VkDevice, uint32_t, uint32_t, VkQueue *);
typedef VkResult (*fn_vkCreateBuffer)(VkDevice, const VkBufferCreateInfo *, const void *, VkBuffer *);
typedef void     (*fn_vkDestroyBuffer)(VkDevice, VkBuffer, const void *);
typedef void     (*fn_vkGetBufferMemoryRequirements)(VkDevice, VkBuffer, VkMemoryRequirements *);
typedef VkResult (*fn_vkAllocateMemory)(VkDevice, const VkMemoryAllocateInfo *, const void *, VkDeviceMemory *);
typedef void     (*fn_vkFreeMemory)(VkDevice, VkDeviceMemory, const void *);
typedef VkResult (*fn_vkBindBufferMemory)(VkDevice, VkBuffer, VkDeviceMemory, VkDeviceSize);
typedef VkResult (*fn_vkCreateDescriptorSetLayout)(VkDevice, const VkDescriptorSetLayoutCreateInfo *, const void *, VkDescriptorSetLayout *);
typedef void     (*fn_vkDestroyDescriptorSetLayout)(VkDevice, VkDescriptorSetLayout, const void *);
typedef VkResult (*fn_vkCreatePipelineLayout)(VkDevice, const VkPipelineLayoutCreateInfo *, const void *, VkPipelineLayout *);
typedef void     (*fn_vkDestroyPipelineLayout)(VkDevice, VkPipelineLayout, const void *);
typedef VkResult (*fn_vkCreateShaderModule)(VkDevice, const VkShaderModuleCreateInfo *, const void *, VkShaderModule *);
typedef void     (*fn_vkDestroyShaderModule)(VkDevice, VkShaderModule, const void *);
typedef VkResult (*fn_vkCreateComputePipelines)(VkDevice, VkPipelineCache, uint32_t, const VkComputePipelineCreateInfo *, const void *, VkPipeline *);
typedef void     (*fn_vkDestroyPipeline)(VkDevice, VkPipeline, const void *);
typedef VkResult (*fn_vkCreateDescriptorPool)(VkDevice, const VkDescriptorPoolCreateInfo *, const void *, VkDescriptorPool *);
typedef void     (*fn_vkDestroyDescriptorPool)(VkDevice, VkDescriptorPool, const void *);
typedef VkResult (*fn_vkAllocateDescriptorSets)(VkDevice, const VkDescriptorSetAllocateInfo *, VkDescriptorSet *);
typedef void     (*fn_vkUpdateDescriptorSets)(VkDevice, uint32_t, const VkWriteDescriptorSet *, uint32_t, const void *);
typedef VkResult (*fn_vkCreateCommandPool)(VkDevice, const VkCommandPoolCreateInfo *, const void *, VkCommandPool *);
typedef void     (*fn_vkDestroyCommandPool)(VkDevice, VkCommandPool, const void *);
typedef VkResult (*fn_vkAllocateCommandBuffers)(VkDevice, const VkCommandBufferAllocateInfo *, VkCommandBuffer *);
typedef VkResult (*fn_vkBeginCommandBuffer)(VkCommandBuffer, const VkCommandBufferBeginInfo *);
typedef VkResult (*fn_vkEndCommandBuffer)(VkCommandBuffer);
typedef void     (*fn_vkCmdBindPipeline)(VkCommandBuffer, uint32_t, VkPipeline);
typedef void     (*fn_vkCmdBindDescriptorSets)(VkCommandBuffer, uint32_t, VkPipelineLayout, uint32_t, uint32_t, const VkDescriptorSet *, uint32_t, const uint32_t *);
typedef void     (*fn_vkCmdDispatch)(VkCommandBuffer, uint32_t, uint32_t, uint32_t);
typedef VkResult (*fn_vkCreateFence)(VkDevice, const VkFenceCreateInfo *, const void *, VkFence *);
typedef void     (*fn_vkDestroyFence)(VkDevice, VkFence, const void *);
typedef VkResult (*fn_vkResetFences)(VkDevice, uint32_t, const VkFence *);
typedef VkResult (*fn_vkWaitForFences)(VkDevice, uint32_t, const VkFence *, uint32_t, uint64_t);
typedef VkResult (*fn_vkQueueSubmit)(VkQueue, uint32_t, const VkSubmitInfo *, VkFence);
typedef VkResult (*fn_vkDeviceWaitIdle)(VkDevice);

static struct {
    void *lib;
    fn_cuInit init;
    fn_cuDeviceGetCount dev_count;
    fn_cuDeviceGet dev_get;
    fn_cuDeviceGetName dev_name;
    fn_cuDeviceGetPCIBusId dev_pci;
    fn_cuDeviceTotalMem_v2 dev_mem;
    fn_cuCtxCreate_v2 ctx_create;
    fn_cuCtxDestroy_v2 ctx_destroy;
    fn_cuCtxSynchronize ctx_sync;
    fn_cuModuleLoadData mod_load;
    fn_cuModuleUnload mod_unload;
    fn_cuModuleGetFunction mod_func;
    fn_cuMemAlloc_v2 mem_alloc;
    fn_cuMemFree_v2 mem_free;
    fn_cuLaunchKernel launch;
} cu;

static struct {
    void *lib;
    fn_vkCreateInstance create_inst;
    fn_vkDestroyInstance destroy_inst;
    fn_vkGetInstanceProcAddr get_proc;
    fn_vkEnumeratePhysicalDevices enum_pdevs;
    fn_vkGetPhysicalDeviceProperties get_props;
    fn_vkGetPhysicalDeviceMemoryProperties get_mem_props;
    fn_vkGetPhysicalDeviceQueueFamilyProperties get_qfam;
    fn_vkCreateDevice create_dev;
    fn_vkDestroyDevice destroy_dev;
    fn_vkGetDeviceQueue get_queue;
    fn_vkCreateBuffer create_buf;
    fn_vkDestroyBuffer destroy_buf;
    fn_vkGetBufferMemoryRequirements get_buf_req;
    fn_vkAllocateMemory alloc_mem;
    fn_vkFreeMemory free_mem;
    fn_vkBindBufferMemory bind_buf_mem;
    fn_vkCreateDescriptorSetLayout create_dsl;
    fn_vkDestroyDescriptorSetLayout destroy_dsl;
    fn_vkCreatePipelineLayout create_pl;
    fn_vkDestroyPipelineLayout destroy_pl;
    fn_vkCreateShaderModule create_sm;
    fn_vkDestroyShaderModule destroy_sm;
    fn_vkCreateComputePipelines create_pipe;
    fn_vkDestroyPipeline destroy_pipe;
    fn_vkCreateDescriptorPool create_dp;
    fn_vkDestroyDescriptorPool destroy_dp;
    fn_vkAllocateDescriptorSets alloc_ds;
    fn_vkUpdateDescriptorSets update_ds;
    fn_vkCreateCommandPool create_cp;
    fn_vkDestroyCommandPool destroy_cp;
    fn_vkAllocateCommandBuffers alloc_cb;
    fn_vkBeginCommandBuffer begin_cb;
    fn_vkEndCommandBuffer end_cb;
    fn_vkCmdBindPipeline cmd_bind_pipe;
    fn_vkCmdBindDescriptorSets cmd_bind_ds;
    fn_vkCmdDispatch cmd_dispatch;
    fn_vkCreateFence create_fence;
    fn_vkDestroyFence destroy_fence;
    fn_vkResetFences reset_fences;
    fn_vkWaitForFences wait_fences;
    fn_vkQueueSubmit queue_submit;
    fn_vkDeviceWaitIdle dev_wait_idle;
} vk;

static const char *PTX_SOURCE =
    ".version 6.0\n"
    ".target sm_50\n"
    ".address_size 64\n"
    ".visible .entry keepalive_kernel(.param .u64 out_ptr, .param .u32 iters)\n"
    "{\n"
    "    .reg .pred %p<2>;\n"
    "    .reg .b32 %r<8>;\n"
    "    .reg .f32 %f<4>;\n"
    "    .reg .b64 %rd<4>;\n"
    "    ld.param.u64 %rd1, [out_ptr];\n"
    "    ld.param.u32 %r1, [iters];\n"
    "    mov.u32 %r2, %ctaid.x;\n"
    "    mov.u32 %r3, %ntid.x;\n"
    "    mov.u32 %r4, %tid.x;\n"
    "    mad.lo.s32 %r5, %r2, %r3, %r4;\n"
    "    cvt.rn.f32.u32 %f1, %r5;\n"
    "    mov.f32 %f2, 0f3F800001;\n"
    "    mov.f32 %f3, 0f3B800000;\n"
    "    mov.u32 %r6, 0;\n"
    "loop:\n"
    "    setp.ge.u32 %p1, %r6, %r1;\n"
    "    @%p1 bra done;\n"
    "    fma.rn.f32 %f1, %f1, %f2, %f3;\n"
    "    fma.rn.f32 %f1, %f1, %f2, %f3;\n"
    "    fma.rn.f32 %f1, %f1, %f2, %f3;\n"
    "    fma.rn.f32 %f1, %f1, %f2, %f3;\n"
    "    add.u32 %r6, %r6, 1;\n"
    "    bra loop;\n"
    "done:\n"
    "    mul.wide.u32 %rd2, %r5, 4;\n"
    "    add.s64 %rd3, %rd1, %rd2;\n"
    "    st.global.f32 [%rd3], %f1;\n"
    "    ret;\n"
    "}\n";

typedef struct {
    const char *backend;
    const char *dev_spec;
    const char *targets_spec;
    unsigned int blocks;
    unsigned int threads;
    uint32_t iters;
    int idle_sleep_ms;
    int yield_ms;
    int adaptive;
    int run_sec;
    int wait_timeout_sec;
    int use_lock;
    int quiet;
    int list_only;
    int list_tsv;
    int status_only;
    int check_vulkan;
    const char *pause_file;
    const char *log_file;
    double t_start;
} config_t;

typedef struct {
    VkPhysicalDevice pdev;
    char name[256];
    char pci_bus_id[32];
    uint32_t device_id;
    uint64_t vram_mib;
    uint32_t qfam;
} vk_gpu_t;

typedef struct {
    int cuda_ord;
    char name[256];
    char pci_bus_id[32];
    uint64_t vram_mib;
} cuda_gpu_t;

typedef struct {
    int index;
    char name[256];
    char pci_bus_id[32];
    uint32_t device_id;
    uint64_t vram_mib;
    int has_vk;
    vk_gpu_t vk_gpu;
    int has_cuda;
    cuda_gpu_t cuda_gpu;
    int is_mining;
    int is_cmp40hx;
} canonical_gpu_t;

typedef struct {
    canonical_gpu_t gpu;
    int use_vk;
    uint32_t iters;
    const config_t *cfg;
} worker_arg_t;

typedef struct { unsigned int gpu; unsigned int memory; } nvml_util_t;
typedef int (*fn_nvmlInit)(void);
typedef int (*fn_nvmlShutdown)(void);
typedef int (*fn_nvmlGetHandle)(unsigned int, void **);
typedef int (*fn_nvmlGetHandleByPci)(const char *, void **);
typedef int (*fn_nvmlGetUtil)(void *, nvml_util_t *);

static struct {
    void *lib;
    fn_nvmlInit init;
    fn_nvmlShutdown shutdown;
    fn_nvmlGetHandle get_handle;
    fn_nvmlGetHandleByPci get_handle_pci;
    fn_nvmlGetUtil get_util;
    int ok;
} nvml;

static void init_nvml_once(void) {
    if (nvml.lib || nvml.ok) return;
    nvml.lib = dlopen("libnvidia-ml.so.1", RTLD_NOW);
    if (!nvml.lib) nvml.lib = dlopen("libnvidia-ml.so", RTLD_NOW);
    if (!nvml.lib) return;
    nvml.init           = (fn_nvmlInit)dlsym(nvml.lib, "nvmlInit_v2");
    nvml.shutdown       = (fn_nvmlShutdown)dlsym(nvml.lib, "nvmlShutdown");
    nvml.get_handle     = (fn_nvmlGetHandle)dlsym(nvml.lib, "nvmlDeviceGetHandleByIndex_v2");
    nvml.get_handle_pci = (fn_nvmlGetHandleByPci)dlsym(nvml.lib, "nvmlDeviceGetHandleByPciBusId_v2");
    nvml.get_util       = (fn_nvmlGetUtil)dlsym(nvml.lib, "nvmlDeviceGetUtilizationRates");
    if (!nvml.init || !nvml.get_handle || !nvml.get_util || nvml.init() != 0) {
        dlclose(nvml.lib);
        nvml.lib = NULL;
        return;
    }
    nvml.ok = 1;
}

static int try_nvml_get_util(int nv_idx, const char *pci_bus_id, void **dev_handle, unsigned int *gpu_u, unsigned int *mem_u) {
    if (!nvml.ok) return 0;
    if (!*dev_handle) {
        int got = 0;
        if (pci_bus_id && pci_bus_id[0] && nvml.get_handle_pci && nvml.get_handle_pci(pci_bus_id, dev_handle) == 0) got = 1;
        if (!got && nvml.get_handle((unsigned int)nv_idx, dev_handle) == 0) got = 1;
        if (!got) return 0;
    }
    nvml_util_t u = {0, 0};
    if (nvml.get_util(*dev_handle, &u) != 0) { *dev_handle = NULL; return 0; }
    *gpu_u = u.gpu; *mem_u = u.memory;
    return 1;
}

static volatile sig_atomic_t g_stop = 0;
static volatile sig_atomic_t g_shutdown = 0;
static volatile sig_atomic_t g_need_restart = 0;
static pthread_mutex_t g_log_lock = PTHREAD_MUTEX_INITIALIZER;

static void on_signal(int sig) {
    (void)sig;
    g_shutdown = 1;
    g_stop = 1;
    g_need_restart = 0;
}

static double now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
}

static void sleep_ms(int ms) {
    if (ms <= 0) return;
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}

static int file_exists(const char *path) {
    if (!path || !*path) return 0;
    struct stat st;
    return stat(path, &st) == 0;
}

static void normalize_pci_id(const char *src, char *dst, size_t dst_sz) {
    if (!src || !*src) { dst[0] = '\0'; return; }
    size_t len = strlen(src);
    if (len == 16 && src[8] == ':') src += 4;
    size_t i = 0;
    for (; src[i] && i + 1 < dst_sz; i++) dst[i] = (char)tolower((unsigned char)src[i]);
    dst[i] = '\0';
}

static void log_event(const config_t *cfg, int to_file, const char *fmt, ...) {
    char msg[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);

    time_t now = time(NULL);
    struct tm tm_now;
    localtime_r(&now, &tm_now);
    char ts[32];
    strftime(ts, sizeof(ts), "%Y-%m-%d %H:%M:%S", &tm_now);

    pthread_mutex_lock(&g_log_lock);
    if (!cfg->quiet) {
        printf("[%s] %s\n", ts, msg);
        fflush(stdout);
    }
    if (to_file && cfg->log_file && cfg->log_file[0]) {
        struct stat st;
        if (stat(cfg->log_file, &st) == 0 && st.st_size > 100 * 1024) {
            char bak[512];
            snprintf(bak, sizeof(bak), "%s.old", cfg->log_file);
            rename(cfg->log_file, bak);
        }
        FILE *f = fopen(cfg->log_file, "a");
        if (f) {
            fprintf(f, "[%s] %s\n", ts, msg);
            fclose(f);
        }
    }
    pthread_mutex_unlock(&g_log_lock);
}

static int contains_ci(const char *hay, const char *needle) {
    size_t nlen = strlen(needle);
    if (nlen == 0) return 1;
    for (const char *p = hay; *p; p++) {
        if (strncasecmp(p, needle, nlen) == 0) return 1;
    }
    return 0;
}

static int is_mining_card(const char *name, uint32_t device_id) {
    static const uint32_t ids[] = {
        0x1F0B, 0x2189, 0x1E09, 0x249D, 0x220D, 0x220A, 0x20B1,
        0x1C07, 0x1C09, 0x1B87, 0x1B07
    };
    if (device_id != 0) {
        for (size_t i = 0; i < sizeof(ids) / sizeof(ids[0]); i++) {
            if (ids[i] == device_id) return 1;
        }
    }
    static const char *patterns[] = { "CMP", "P106", "P104", "P102" };
    if (name) {
        for (size_t i = 0; i < sizeof(patterns) / sizeof(patterns[0]); i++) {
            if (contains_ci(name, patterns[i])) return 1;
        }
    }
    return 0;
}

static int is_cmp40hx_card(const char *name, uint32_t device_id) {
    if (device_id == 0x1F0B) return 1;
    if (name && contains_ci(name, "CMP 40HX")) return 1;
    return 0;
}

/* ===================== Dynamic library loaders ===================== */
static int load_cuda(void) {
    if (cu.lib) return 1;
    cu.lib = dlopen("libcuda.so.1", RTLD_NOW);
    if (!cu.lib) cu.lib = dlopen("libcuda.so", RTLD_NOW);
    if (!cu.lib) return 0;

    cu.init        = (fn_cuInit)dlsym(cu.lib, "cuInit");
    cu.dev_count   = (fn_cuDeviceGetCount)dlsym(cu.lib, "cuDeviceGetCount");
    cu.dev_get     = (fn_cuDeviceGet)dlsym(cu.lib, "cuDeviceGet");
    cu.dev_name    = (fn_cuDeviceGetName)dlsym(cu.lib, "cuDeviceGetName");
    cu.dev_pci     = (fn_cuDeviceGetPCIBusId)dlsym(cu.lib, "cuDeviceGetPCIBusId");
    cu.dev_mem     = (fn_cuDeviceTotalMem_v2)dlsym(cu.lib, "cuDeviceTotalMem_v2");
    cu.ctx_create  = (fn_cuCtxCreate_v2)dlsym(cu.lib, "cuCtxCreate_v2");
    cu.ctx_destroy = (fn_cuCtxDestroy_v2)dlsym(cu.lib, "cuCtxDestroy_v2");
    cu.ctx_sync    = (fn_cuCtxSynchronize)dlsym(cu.lib, "cuCtxSynchronize");
    cu.mod_load    = (fn_cuModuleLoadData)dlsym(cu.lib, "cuModuleLoadData");
    cu.mod_unload  = (fn_cuModuleUnload)dlsym(cu.lib, "cuModuleUnload");
    cu.mod_func    = (fn_cuModuleGetFunction)dlsym(cu.lib, "cuModuleGetFunction");
    cu.mem_alloc   = (fn_cuMemAlloc_v2)dlsym(cu.lib, "cuMemAlloc_v2");
    cu.mem_free    = (fn_cuMemFree_v2)dlsym(cu.lib, "cuMemFree_v2");
    cu.launch      = (fn_cuLaunchKernel)dlsym(cu.lib, "cuLaunchKernel");

    if (!cu.init || !cu.dev_count || !cu.dev_get || !cu.dev_name ||
        !cu.ctx_create || !cu.ctx_destroy || !cu.ctx_sync || !cu.mod_load ||
        !cu.mod_unload || !cu.mod_func || !cu.mem_alloc || !cu.mem_free || !cu.launch) {
        dlclose(cu.lib);
        cu.lib = NULL;
        return 0;
    }
    return 1;
}

static int load_vulkan(void) {
    if (vk.lib) return 1;
    vk.lib = dlopen("libvulkan.so.1", RTLD_NOW);
    if (!vk.lib) vk.lib = dlopen("libvulkan.so", RTLD_NOW);
    if (!vk.lib) return 0;

    #define VK_SYM(field, name) vk.field = (fn_##name)dlsym(vk.lib, #name); if (!vk.field) goto fail;
    VK_SYM(create_inst, vkCreateInstance)
    VK_SYM(destroy_inst, vkDestroyInstance)
    VK_SYM(enum_pdevs, vkEnumeratePhysicalDevices)
    VK_SYM(get_props, vkGetPhysicalDeviceProperties)
    VK_SYM(get_mem_props, vkGetPhysicalDeviceMemoryProperties)
    VK_SYM(get_qfam, vkGetPhysicalDeviceQueueFamilyProperties)
    VK_SYM(create_dev, vkCreateDevice)
    VK_SYM(destroy_dev, vkDestroyDevice)
    VK_SYM(get_queue, vkGetDeviceQueue)
    VK_SYM(create_buf, vkCreateBuffer)
    VK_SYM(destroy_buf, vkDestroyBuffer)
    VK_SYM(get_buf_req, vkGetBufferMemoryRequirements)
    VK_SYM(alloc_mem, vkAllocateMemory)
    VK_SYM(free_mem, vkFreeMemory)
    VK_SYM(bind_buf_mem, vkBindBufferMemory)
    VK_SYM(create_dsl, vkCreateDescriptorSetLayout)
    VK_SYM(destroy_dsl, vkDestroyDescriptorSetLayout)
    VK_SYM(create_pl, vkCreatePipelineLayout)
    VK_SYM(destroy_pl, vkDestroyPipelineLayout)
    VK_SYM(create_sm, vkCreateShaderModule)
    VK_SYM(destroy_sm, vkDestroyShaderModule)
    VK_SYM(create_pipe, vkCreateComputePipelines)
    VK_SYM(destroy_pipe, vkDestroyPipeline)
    VK_SYM(create_dp, vkCreateDescriptorPool)
    VK_SYM(destroy_dp, vkDestroyDescriptorPool)
    VK_SYM(alloc_ds, vkAllocateDescriptorSets)
    VK_SYM(update_ds, vkUpdateDescriptorSets)
    VK_SYM(create_cp, vkCreateCommandPool)
    VK_SYM(destroy_cp, vkDestroyCommandPool)
    VK_SYM(alloc_cb, vkAllocateCommandBuffers)
    VK_SYM(begin_cb, vkBeginCommandBuffer)
    VK_SYM(end_cb, vkEndCommandBuffer)
    VK_SYM(cmd_bind_pipe, vkCmdBindPipeline)
    VK_SYM(cmd_bind_ds, vkCmdBindDescriptorSets)
    VK_SYM(cmd_dispatch, vkCmdDispatch)
    VK_SYM(create_fence, vkCreateFence)
    VK_SYM(destroy_fence, vkDestroyFence)
    VK_SYM(reset_fences, vkResetFences)
    VK_SYM(wait_fences, vkWaitForFences)
    VK_SYM(queue_submit, vkQueueSubmit)
    VK_SYM(dev_wait_idle, vkDeviceWaitIdle)
    #undef VK_SYM
    vk.get_proc = (fn_vkGetInstanceProcAddr)dlsym(vk.lib, "vkGetInstanceProcAddr");
    return 1;
fail:
    dlclose(vk.lib);
    vk.lib = NULL;
    return 0;
}

/* ===================== SPIR-V 1.0 Builder ===================== */
static uint32_t float_bits(float f) {
    uint32_t u;
    memcpy(&u, &f, 4);
    return u;
}

static size_t build_spirv(uint32_t *w, uint32_t local_x, uint32_t iters) {
    size_t n = 0;
    #define E0(op) do { w[n++] = (1u << 16) | (op); } while (0)
    #define E1(op,a) do { w[n++] = (2u << 16) | (op); w[n++] = (a); } while (0)
    #define E2(op,a,b) do { w[n++] = (3u << 16) | (op); w[n++] = (a); w[n++] = (b); } while (0)
    #define E3(op,a,b,c) do { w[n++] = (4u << 16) | (op); w[n++] = (a); w[n++] = (b); w[n++] = (c); } while (0)
    #define E4(op,a,b,c,d) do { w[n++] = (5u << 16) | (op); w[n++] = (a); w[n++] = (b); w[n++] = (c); w[n++] = (d); } while (0)
    #define E5(op,a,b,c,d,e) do { w[n++] = (6u << 16) | (op); w[n++] = (a); w[n++] = (b); w[n++] = (c); w[n++] = (d); w[n++] = (e); } while (0)

    w[n++] = 0x07230203u; w[n++] = 0x00010000u; w[n++] = 0; w[n++] = 50; w[n++] = 0;

    enum {
        idMain = 1, idVoid, idFnType,
        idU32, idF32, idBool, idV3U32,
        idPtrInV3U32, idPtrInU32, idGidVar,
        idRtArrF32, idBufStruct, idPtrUniformBuf, idBufVar, idPtrUniformF32,
        idC0, idC1, idCIters, idCFMul, idCFAdd,
        idPtrFnU32, idPtrFnF32,
        idLblEntry, idVarI, idVarF, idGidPtr, idGidVal, idFInit,
        idLblHead, idLblBody, idLblCont, idLblMerge,
        idILoad, idCmp,
        idFLoad, idFM1, idFA1, idFM2, idFA2, idFM3, idFA3, idFM4, idFA4,
        idINext, idFFinal, idOutElemPtr
    };

    E1(17, 1); E2(14, 0, 1); E5(15, 5, idMain, 0x6E69616Du, 0u, idGidVar); E5(16, idMain, 17, local_x, 1, 1);
    E3(71, idGidVar, 11, 28); E3(71, idRtArrF32, 6, 4); E4(72, idBufStruct, 0, 35, 0);
    E2(71, idBufStruct, 3); E3(71, idBufVar, 34, 0); E3(71, idBufVar, 33, 0);

    E1(19, idVoid); E2(33, idFnType, idVoid); E3(21, idU32, 32, 0); E2(22, idF32, 32); E1(20, idBool);
    E3(23, idV3U32, idU32, 3); E3(32, idPtrInV3U32, 1, idV3U32); E3(32, idPtrInU32, 1, idU32);
    E2(29, idRtArrF32, idF32); E2(30, idBufStruct, idRtArrF32); E3(32, idPtrUniformBuf, 2, idBufStruct);
    E3(32, idPtrUniformF32, 2, idF32); E3(32, idPtrFnU32, 7, idU32); E3(32, idPtrFnF32, 7, idF32);

    E3(43, idU32, idC0, 0); E3(43, idU32, idC1, 1); E3(43, idU32, idCIters, iters);
    E3(43, idF32, idCFMul, float_bits(1.000001f)); E3(43, idF32, idCFAdd, float_bits(0.0009765625f));
    E3(59, idPtrInV3U32, idGidVar, 1); E3(59, idPtrUniformBuf, idBufVar, 2);

    E4(54, idVoid, idMain, 0, idFnType); E1(248, idLblEntry);
    E3(59, idPtrFnU32, idVarI, 7); E3(59, idPtrFnF32, idVarF, 7);
    E4(65, idPtrInU32, idGidPtr, idGidVar, idC0); E3(61, idU32, idGidVal, idGidPtr);
    E3(112, idF32, idFInit, idGidVal); E2(62, idVarF, idFInit); E2(62, idVarI, idC0); E1(249, idLblHead);

    E1(248, idLblHead); E3(246, idLblMerge, idLblCont, 0); E3(61, idU32, idILoad, idVarI);
    E4(176, idBool, idCmp, idILoad, idCIters); E3(250, idCmp, idLblBody, idLblMerge);

    E1(248, idLblBody); E3(61, idF32, idFLoad, idVarF);
    E4(133, idF32, idFM1, idFLoad, idCFMul); E4(129, idF32, idFA1, idFM1, idCFAdd);
    E4(133, idF32, idFM2, idFA1,   idCFMul); E4(129, idF32, idFA2, idFM2, idCFAdd);
    E4(133, idF32, idFM3, idFA2,   idCFMul); E4(129, idF32, idFA3, idFM3, idCFAdd);
    E4(133, idF32, idFM4, idFA3,   idCFMul); E4(129, idF32, idFA4, idFM4, idCFAdd);
    E2(62, idVarF, idFA4); E1(249, idLblCont);

    E1(248, idLblCont); E4(128, idU32, idINext, idILoad, idC1); E2(62, idVarI, idINext); E1(249, idLblHead);

    E1(248, idLblMerge); E3(61, idF32, idFFinal, idVarF);
    E5(65, idPtrUniformF32, idOutElemPtr, idBufVar, idC0, idGidVal); E2(62, idOutElemPtr, idFFinal);
    E0(253); E0(56);

    #undef E0
    #undef E1
    #undef E2
    #undef E3
    #undef E4
    #undef E5
    return n;
}

static VkInstance create_vk_instance(void) {
    if (!load_vulkan()) return NULL;
    uint32_t vers[2] = { (1u << 22) | (1u << 12), (1u << 22) };
    for (int v = 0; v < 2; v++) {
        VkApplicationInfo ai = { 0, NULL, "NV-KeepAlive", 1, NULL, 0, vers[v] };
        VkInstanceCreateInfo ici = { 1, NULL, 0, &ai, 0, NULL, 0, NULL };
        VkInstance inst = NULL;
        if (vk.create_inst(&ici, NULL, &inst) == 0 && inst) return inst;
    }
    return NULL;
}

static void try_vk_pci_bus_id(VkInstance inst, VkPhysicalDevice pdev, char *out, size_t out_sz) {
    out[0] = '\0';
    if (!vk.get_proc) return;
    fn_vkGetPhysicalDeviceProperties2 fn = (fn_vkGetPhysicalDeviceProperties2)vk.get_proc(inst, "vkGetPhysicalDeviceProperties2");
    if (!fn) fn = (fn_vkGetPhysicalDeviceProperties2)vk.get_proc(inst, "vkGetPhysicalDeviceProperties2KHR");
    if (!fn) return;
    VkPhysicalDevicePCIBusInfoPropertiesEXT pci;
    memset(&pci, 0, sizeof(pci));
    pci.sType = 1000212000u;
    pci.pciDomain = 0xFFFFFFFFu;
    uint8_t props2[1024];
    memset(props2, 0, sizeof(props2));
    uint32_t st = 1000059001u;
    memcpy(props2, &st, 4);
    void *pnext = &pci;
    memcpy(props2 + sizeof(void *), &pnext, sizeof(void *));
    fn(pdev, props2);
    if (pci.pciDomain != 0xFFFFFFFFu && pci.pciBus <= 255 && pci.pciDevice <= 31 && pci.pciFunction <= 7) {
        snprintf(out, out_sz, "%04x:%02x:%02x.%u", pci.pciDomain & 0xFFFFu, pci.pciBus, pci.pciDevice, pci.pciFunction);
    }
}

static int enum_vk_nvidia(VkInstance inst, vk_gpu_t *out, int max_out) {
    if (!inst) return 0;
    uint32_t count = 0;
    if (vk.enum_pdevs(inst, &count, NULL) != 0 || count == 0) return 0;
    if (count > 64) count = 64;
    VkPhysicalDevice pdevs[64];
    if (vk.enum_pdevs(inst, &count, pdevs) != 0) return 0;

    int found = 0;
    uint8_t props[1024], mprops[1024];
    for (uint32_t i = 0; i < count && found < max_out; i++) {
        memset(props, 0, sizeof(props));
        vk.get_props(pdevs[i], props);
        uint32_t vendor_id = 0, device_id = 0;
        memcpy(&vendor_id, props + 8, 4);
        if (vendor_id != 0x10DEu) continue;
        memcpy(&device_id, props + 12, 4);

        uint32_t qcount = 0;
        vk.get_qfam(pdevs[i], &qcount, NULL);
        if (qcount == 0) continue;
        if (qcount > 32) qcount = 32;
        VkQueueFamilyProperties qprops[32];
        vk.get_qfam(pdevs[i], &qcount, qprops);
        uint32_t qfam = UINT32_MAX;
        for (uint32_t q = 0; q < qcount; q++) {
            if (qprops[q].queueFlags & 0x2u) { qfam = q; break; }
        }
        if (qfam == UINT32_MAX) continue;

        memset(mprops, 0, sizeof(mprops));
        vk.get_mem_props(pdevs[i], mprops);
        uint32_t hcount = 0;
        memcpy(&hcount, mprops + 4 + 32 * 8, 4);
        uint64_t vram = 0;
        for (uint32_t h = 0; h < hcount && h < 16; h++) {
            uint64_t hsz = 0; uint32_t hfl = 0;
            size_t off = 4 + 32 * 8 + 4 + (size_t)h * 16;
            memcpy(&hsz, mprops + off, 8);
            memcpy(&hfl, mprops + off + 8, 4);
            if ((hfl & 1u) && hsz > vram) vram = hsz;
        }

        vk_gpu_t *g = &out[found++];
        memset(g, 0, sizeof(*g));
        g->pdev = pdevs[i];
        memcpy(g->name, props + 20, 255);
        g->name[255] = '\0';
        g->device_id = device_id;
        g->vram_mib = vram / (1024ULL * 1024ULL);
        g->qfam = qfam;
        try_vk_pci_bus_id(inst, pdevs[i], g->pci_bus_id, sizeof(g->pci_bus_id));
    }
    return found;
}

static int enum_cuda_gpus(cuda_gpu_t *out, int max_out) {
    if (!load_cuda()) return 0;
    int count = 0;
    if (cu.init(0) != 0 || cu.dev_count(&count) != 0 || count <= 0) return 0;
    int found = 0;
    for (int i = 0; i < count && found < max_out; i++) {
        CUdevice d;
        if (cu.dev_get(&d, i) != 0) continue;
        cuda_gpu_t *g = &out[found++];
        memset(g, 0, sizeof(*g));
        g->cuda_ord = i;
        strcpy(g->name, "Unknown");
        cu.dev_name(g->name, sizeof(g->name) - 1, d);
        if (cu.dev_pci) {
            char raw[64] = {0};
            if (cu.dev_pci(raw, sizeof(raw) - 1, d) == 0) normalize_pci_id(raw, g->pci_bus_id, sizeof(g->pci_bus_id));
        }
        size_t mem = 0;
        if (cu.dev_mem && cu.dev_mem(&mem, d) == 0) g->vram_mib = (uint64_t)(mem / (1024 * 1024));
    }
    return found;
}

static int cmp_canonical(const void *pa, const void *pb) {
    const canonical_gpu_t *a = (const canonical_gpu_t *)pa;
    const canonical_gpu_t *b = (const canonical_gpu_t *)pb;
    int ha = a->pci_bus_id[0] != '\0';
    int hb = b->pci_bus_id[0] != '\0';
    if (ha && hb) return strcasecmp(a->pci_bus_id, b->pci_bus_id);
    if (ha) return -1;
    if (hb) return 1;
    return strcasecmp(a->name, b->name);
}

static int probe_canonical_gpus(VkInstance *out_inst, canonical_gpu_t *out, int max_out) {
    *out_inst = create_vk_instance();
    vk_gpu_t vgs[64]; int vn = 0;
    if (*out_inst) vn = enum_vk_nvidia(*out_inst, vgs, 64);
    cuda_gpu_t cgs[64]; int cn = enum_cuda_gpus(cgs, 64);
    int used_c[64]; memset(used_c, 0, sizeof(used_c));
    int n = 0;

    for (int i = 0; i < vn && n < max_out; i++) {
        canonical_gpu_t *cg = &out[n++];
        memset(cg, 0, sizeof(*cg));
        strncpy(cg->name, vgs[i].name, sizeof(cg->name) - 1);
        strncpy(cg->pci_bus_id, vgs[i].pci_bus_id, sizeof(cg->pci_bus_id) - 1);
        cg->device_id = vgs[i].device_id;
        cg->vram_mib = vgs[i].vram_mib;
        cg->has_vk = 1;
        cg->vk_gpu = vgs[i];
        int match = -1;
        if (vgs[i].pci_bus_id[0]) {
            for (int j = 0; j < cn; j++) if (!used_c[j] && strcasecmp(cgs[j].pci_bus_id, vgs[i].pci_bus_id) == 0) { match = j; break; }
        }
        if (match < 0) {
            for (int j = 0; j < cn; j++) if (!used_c[j] && strcasecmp(cgs[j].name, vgs[i].name) == 0) { match = j; break; }
        }
        if (match >= 0) {
            used_c[match] = 1;
            cg->has_cuda = 1;
            cg->cuda_gpu = cgs[match];
            if (!cg->pci_bus_id[0]) strncpy(cg->pci_bus_id, cgs[match].pci_bus_id, sizeof(cg->pci_bus_id) - 1);
        }
    }
    for (int j = 0; j < cn && n < max_out; j++) {
        if (used_c[j]) continue;
        canonical_gpu_t *cg = &out[n++];
        memset(cg, 0, sizeof(*cg));
        strncpy(cg->name, cgs[j].name, sizeof(cg->name) - 1);
        strncpy(cg->pci_bus_id, cgs[j].pci_bus_id, sizeof(cg->pci_bus_id) - 1);
        cg->vram_mib = cgs[j].vram_mib;
        cg->has_cuda = 1;
        cg->cuda_gpu = cgs[j];
    }

    qsort(out, (size_t)n, sizeof(canonical_gpu_t), cmp_canonical);
    for (int i = 0; i < n; i++) {
        out[i].index = i;
        out[i].is_mining = is_mining_card(out[i].name, out[i].device_id);
        out[i].is_cmp40hx = is_cmp40hx_card(out[i].name, out[i].device_id);
    }
    return n;
}

/* ===================== Workers ===================== */
static void *vulkan_worker(void *arg_ptr) {
    worker_arg_t *wa = (worker_arg_t *)arg_ptr;
    const config_t *cfg = wa->cfg;
    canonical_gpu_t cgpu = wa->gpu;

    uint32_t spirv[512];
    size_t spv_words = build_spirv(spirv, cfg->threads, wa->iters);
    uint64_t launches = 0, yields = 0;
    int recoveries = 0, consecutive_failures = 0;

    while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
        if (file_exists(cfg->pause_file)) {
            log_event(cfg, 1, "[GPU %d] Paused (file: %s) - Vulkan context released.", cgpu.index, cfg->pause_file);
            while (!g_stop && file_exists(cfg->pause_file) &&
                   (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
                sleep_ms(500);
            }
            if (g_stop) break;
            log_event(cfg, 1, "[GPU %d] Pause cleared, resuming.", cgpu.index);
            sleep_ms(500);
            continue;
        }

        VkInstance local_inst = NULL;
        VkDevice dev = NULL; VkBuffer buf = 0; VkDeviceMemory mem = 0;
        VkDescriptorSetLayout dsl = 0; VkPipelineLayout pl = 0;
        VkShaderModule sm = 0; VkPipeline pipe = 0;
        VkDescriptorPool dp = 0; VkDescriptorSet ds = 0;
        VkCommandPool cp = 0; VkCommandBuffer cb = NULL;
        VkFence fence = 0; VkQueue queue = NULL;
        VkResult rc = 0;
        vk_gpu_t gpu = cgpu.vk_gpu;

        local_inst = create_vk_instance();
        if (!local_inst) { rc = -1; goto vk_err; }
        vk_gpu_t fresh[64];
        int nf = enum_vk_nvidia(local_inst, fresh, 64);
        if (nf <= 0) { rc = -2; goto vk_err; }
        int matched = -1;
        if (cgpu.pci_bus_id[0]) {
            for (int i = 0; i < nf; i++) {
                if (strcasecmp(fresh[i].pci_bus_id, cgpu.pci_bus_id) == 0) { matched = i; break; }
            }
        }
        if (matched < 0 && cgpu.device_id != 0) {
            for (int i = 0; i < nf; i++) {
                if (fresh[i].device_id == cgpu.device_id) { matched = i; break; }
            }
        }
        if (matched < 0) matched = (cgpu.index >= 0 && cgpu.index < nf) ? cgpu.index : 0;
        gpu = fresh[matched];

        float prio = 1.0f;
        VkDeviceQueueCreateInfo qci = { 2, NULL, 0, gpu.qfam, 1, &prio };
        VkDeviceCreateInfo dci = { 3, NULL, 0, 1, &qci, 0, NULL, 0, NULL, NULL };
        if ((rc = vk.create_dev(gpu.pdev, &dci, NULL, &dev)) != 0) goto vk_err;
        vk.get_queue(dev, gpu.qfam, 0, &queue);

        VkDeviceSize buf_bytes = (VkDeviceSize)cfg->blocks * cfg->threads * 4ULL;
        VkBufferCreateInfo bci = { 12, NULL, 0, buf_bytes, 0x20u, 0, 0, NULL };
        if ((rc = vk.create_buf(dev, &bci, NULL, &buf)) != 0) goto vk_err;
        VkMemoryRequirements req;
        vk.get_buf_req(dev, buf, &req);

        uint8_t mprops[1024]; memset(mprops, 0, sizeof(mprops));
        vk.get_mem_props(gpu.pdev, mprops);
        uint32_t mcount = 0, midx = 0;
        memcpy(&mcount, mprops, 4);
        for (uint32_t i = 0; i < mcount && i < 32; i++) {
            uint32_t fl = 0; memcpy(&fl, mprops + 4 + i * 8, 4);
            if ((req.memoryTypeBits & (1u << i)) && (fl & 1u)) { midx = i; break; }
        }
        VkMemoryAllocateInfo mai = { 5, NULL, req.size, midx };
        if ((rc = vk.alloc_mem(dev, &mai, NULL, &mem)) != 0) goto vk_err;
        if ((rc = vk.bind_buf_mem(dev, buf, mem, 0)) != 0) goto vk_err;

        VkDescriptorSetLayoutBinding bind = { 0, 7, 1, 0x20u, NULL };
        VkDescriptorSetLayoutCreateInfo dslci = { 32, NULL, 0, 1, &bind };
        if ((rc = vk.create_dsl(dev, &dslci, NULL, &dsl)) != 0) goto vk_err;
        VkPipelineLayoutCreateInfo plci = { 30, NULL, 0, 1, &dsl, 0, NULL };
        if ((rc = vk.create_pl(dev, &plci, NULL, &pl)) != 0) goto vk_err;
        VkShaderModuleCreateInfo smci = { 16, NULL, 0, spv_words * sizeof(uint32_t), spirv };
        if ((rc = vk.create_sm(dev, &smci, NULL, &sm)) != 0) goto vk_err;
        VkComputePipelineCreateInfo cpci = { 29, NULL, 0, { 18, NULL, 0, 0x20u, sm, "main", NULL }, pl, 0, -1 };
        if ((rc = vk.create_pipe(dev, 0, 1, &cpci, NULL, &pipe)) != 0) goto vk_err;

        VkDescriptorPoolSize dps = { 7, 1 };
        VkDescriptorPoolCreateInfo dpci = { 33, NULL, 0, 1, 1, &dps };
        if ((rc = vk.create_dp(dev, &dpci, NULL, &dp)) != 0) goto vk_err;
        VkDescriptorSetAllocateInfo dsai = { 34, NULL, dp, 1, &dsl };
        if ((rc = vk.alloc_ds(dev, &dsai, &ds)) != 0) goto vk_err;
        VkDescriptorBufferInfo dbi = { buf, 0, buf_bytes };
        VkWriteDescriptorSet wds = { 35, NULL, ds, 0, 0, 1, 7, NULL, &dbi, NULL };
        vk.update_ds(dev, 1, &wds, 0, NULL);

        VkCommandPoolCreateInfo cp_ci = { 39, NULL, 0, gpu.qfam };
        if ((rc = vk.create_cp(dev, &cp_ci, NULL, &cp)) != 0) goto vk_err;
        VkCommandBufferAllocateInfo cbai = { 40, NULL, cp, 0, 1 };
        if ((rc = vk.alloc_cb(dev, &cbai, &cb)) != 0) goto vk_err;
        VkCommandBufferBeginInfo cbbi = { 42, NULL, 0, NULL };
        if ((rc = vk.begin_cb(cb, &cbbi)) != 0) goto vk_err;
        vk.cmd_bind_pipe(cb, 1, pipe);
        vk.cmd_bind_ds(cb, 1, pl, 0, 1, &ds, 0, NULL);
        vk.cmd_dispatch(cb, cfg->blocks, 1, 1);
        if ((rc = vk.end_cb(cb)) != 0) goto vk_err;

        VkFenceCreateInfo fci = { 8, NULL, 0 };
        if ((rc = vk.create_fence(dev, &fci, NULL, &fence)) != 0) goto vk_err;

        log_event(cfg, 1, "[GPU %d] Active (Vulkan): %s [DEV=0x%04X] (PCI %s, buf=%llu B, iters=%u)",
                  cgpu.index, cgpu.name, cgpu.device_id, cgpu.pci_bus_id[0] ? cgpu.pci_bus_id : "n/a",
                  (unsigned long long)buf_bytes, wa->iters);

        VkSubmitInfo si = { 4, NULL, 0, NULL, NULL, 1, &cb, 0, NULL };
        double base_ms = 0.0;
        int calib_count = 0, idle_streak = 0, was_yielding = 0;
        double last_pause_check = now_ms(), last_nvml_check = now_ms();
        void *nvml_dev = NULL;

        while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
            double t0 = now_ms();
            if (t0 - last_pause_check >= 2000.0) {
                last_pause_check = t0;
                if (file_exists(cfg->pause_file)) break;
            }
            if (cfg->adaptive && (t0 - last_nvml_check >= 250.0)) {
                last_nvml_check = t0;
                unsigned int u_gpu = 0, u_mem = 0;
                if (try_nvml_get_util(cgpu.index, cgpu.pci_bus_id, &nvml_dev, &u_gpu, &u_mem) && u_mem >= 5) {
                    yields++;
                    log_event(cfg, 0, "[GPU %d] External load (mem=%u%%, gpu=%u%%) -> yielding completely", cgpu.index, u_mem, u_gpu);
                    while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
                        sleep_ms(250);
                        double tn = now_ms();
                        if (tn - last_pause_check >= 2000.0) {
                            last_pause_check = tn;
                            if (file_exists(cfg->pause_file)) break;
                        }
                        if (!try_nvml_get_util(cgpu.index, cgpu.pci_bus_id, &nvml_dev, &u_gpu, &u_mem)) break;
                        if (u_mem < 5 && u_gpu < 15) break;
                    }
                    if (g_stop || file_exists(cfg->pause_file)) break;
                    log_event(cfg, 0, "[GPU %d] External load finished -> active keepalive", cgpu.index);
                    last_nvml_check = now_ms();
                    continue;
                }
            }
            if ((rc = vk.reset_fences(dev, 1, &fence)) != 0) goto vk_err;
            if ((rc = vk.queue_submit(queue, 1, &si, fence)) != 0) goto vk_err;
            if ((rc = vk.wait_fences(dev, 1, &fence, 1, 5000000000ULL)) != 0) goto vk_err;
            double dt = now_ms() - t0;
            launches++;
            consecutive_failures = 0;

            if (launches > 5 && calib_count < 20) {
                base_ms = (base_ms * calib_count + dt) / (calib_count + 1);
                calib_count++;
            } else if (cfg->adaptive && calib_count >= 20) {
                if (!was_yielding && dt < base_ms) base_ms = base_ms * 0.9 + dt * 0.1;
                double thresh = base_ms * 1.85;
                if (base_ms + 0.85 > thresh) thresh = base_ms + 0.85;
                if (dt > thresh) {
                    yields++; idle_streak = 0;
                    if (!was_yielding) {
                        was_yielding = 1;
                        log_event(cfg, 0, "[GPU %d] Busy (%.2fms vs %.2fms) -> yield %dms", cgpu.index, dt, base_ms, cfg->yield_ms);
                    }
                    sleep_ms(cfg->yield_ms);
                    continue;
                } else if (was_yielding) {
                    idle_streak++;
                    if (idle_streak < 3) continue;
                    was_yielding = 0;
                    log_event(cfg, 0, "[GPU %d] Idle (%.2fms) -> active keepalive", cgpu.index, dt);
                }
            }
            if (cfg->idle_sleep_ms > 0) sleep_ms(cfg->idle_sleep_ms);
        }
        goto vk_cleanup;

    vk_err:
        recoveries++;
        consecutive_failures++;
        log_event(cfg, 1, "[GPU %d] Vulkan recovering (#%d, rc=%d)", cgpu.index, recoveries, rc);
        if (consecutive_failures >= 3 && cfg->run_sec == 0 && !g_stop) {
            log_event(cfg, 1, "[GPU %d] Vulkan handle lost after reset (rc=%d) - re-execing fresh process...", cgpu.index, rc);
            g_need_restart = 1;
            g_stop = 1;
        }
    vk_cleanup:
        if (dev) {
            vk.dev_wait_idle(dev);
            if (fence) vk.destroy_fence(dev, fence, NULL);
            if (cp)    vk.destroy_cp(dev, cp, NULL);
            if (dp)    vk.destroy_dp(dev, dp, NULL);
            if (pipe)  vk.destroy_pipe(dev, pipe, NULL);
            if (sm)    vk.destroy_sm(dev, sm, NULL);
            if (pl)    vk.destroy_pl(dev, pl, NULL);
            if (dsl)   vk.destroy_dsl(dev, dsl, NULL);
            if (buf)   vk.destroy_buf(dev, buf, NULL);
            if (mem)   vk.free_mem(dev, mem, NULL);
            vk.destroy_dev(dev, NULL);
        }
        if (local_inst) vk.destroy_inst(local_inst, NULL);
        if (rc != 0 && !g_stop) sleep_ms(1500);
    }

    log_event(cfg, 1, "[GPU %d] Stopped (Vulkan): launches=%llu, yields=%llu, recoveries=%d, elapsed=%.1fs",
              cgpu.index, (unsigned long long)launches, (unsigned long long)yields, recoveries, (now_ms() - cfg->t_start) / 1000.0);
    return NULL;
}

static void *cuda_worker(void *arg_ptr) {
    worker_arg_t *wa = (worker_arg_t *)arg_ptr;
    const config_t *cfg = wa->cfg;
    canonical_gpu_t cgpu = wa->gpu;
    int ordinal = cgpu.cuda_gpu.cuda_ord;

    uint64_t launches = 0, yields = 0;
    int recoveries = 0, consecutive_failures = 0;

    while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
        if (file_exists(cfg->pause_file)) {
            log_event(cfg, 1, "[GPU %d] Paused (file: %s) - CUDA context released.", cgpu.index, cfg->pause_file);
            while (!g_stop && file_exists(cfg->pause_file) &&
                   (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
                sleep_ms(500);
            }
            if (g_stop) break;
            log_event(cfg, 1, "[GPU %d] Pause cleared, resuming.", cgpu.index);
            continue;
        }

        CUdevice dev = 0; CUcontext ctx = NULL; CUmodule mod = NULL; CUfunction fn = NULL; CUdeviceptr d_out = 0;
        CUresult rc;
        size_t buf_bytes = (size_t)cfg->blocks * (size_t)cfg->threads * sizeof(float);
        if ((rc = cu.dev_get(&dev, ordinal)) != 0 ||
            (rc = cu.ctx_create(&ctx, 4, dev)) != 0 ||
            (rc = cu.mod_load(&mod, PTX_SOURCE)) != 0 ||
            (rc = cu.mod_func(&fn, mod, "keepalive_kernel")) != 0 ||
            (rc = cu.mem_alloc(&d_out, buf_bytes)) != 0) {
            recoveries++;
            consecutive_failures++;
            log_event(cfg, 1, "[GPU %d] CUDA recovering (#%d, rc=%d)", cgpu.index, recoveries, rc);
            if (d_out) cu.mem_free(d_out);
            if (mod) cu.mod_unload(mod);
            if (ctx) cu.ctx_destroy(ctx);
            if (consecutive_failures >= 3 && cfg->run_sec == 0 && !g_stop) {
                log_event(cfg, 1, "[GPU %d] CUDA handle poisoned after reset (rc=%d) - re-execing fresh process...", cgpu.index, rc);
                g_need_restart = 1;
                g_stop = 1;
                break;
            }
            sleep_ms(1500);
            continue;
        }
        log_event(cfg, 1, "[GPU %d] Active (CUDA): %s (PCI %s, buf=%zu B, iters=%u)",
                  cgpu.index, cgpu.name, cgpu.pci_bus_id[0] ? cgpu.pci_bus_id : "n/a", buf_bytes, wa->iters);

        uint32_t iters_val = wa->iters;
        void *kparams[2] = { &d_out, &iters_val };
        double base_ms = 0.0;
        int calib_count = 0, idle_streak = 0, was_yielding = 0;
        double last_pause_check = now_ms(), last_nvml_check = now_ms();
        void *nvml_dev = NULL;

        while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
            double t0 = now_ms();
            if (t0 - last_pause_check >= 2000.0) {
                last_pause_check = t0;
                if (file_exists(cfg->pause_file)) break;
            }
            if (cfg->adaptive && (t0 - last_nvml_check >= 250.0)) {
                last_nvml_check = t0;
                unsigned int u_gpu = 0, u_mem = 0;
                if (try_nvml_get_util(cgpu.index, cgpu.pci_bus_id, &nvml_dev, &u_gpu, &u_mem) && u_mem >= 5) {
                    yields++;
                    log_event(cfg, 0, "[GPU %d] External load (mem=%u%%, gpu=%u%%) -> yielding completely", cgpu.index, u_mem, u_gpu);
                    while (!g_stop && (cfg->run_sec == 0 || (now_ms() - cfg->t_start) < (double)cfg->run_sec * 1000.0)) {
                        sleep_ms(250);
                        double tn = now_ms();
                        if (tn - last_pause_check >= 2000.0) {
                            last_pause_check = tn;
                            if (file_exists(cfg->pause_file)) break;
                        }
                        if (!try_nvml_get_util(cgpu.index, cgpu.pci_bus_id, &nvml_dev, &u_gpu, &u_mem)) break;
                        if (u_mem < 5 && u_gpu < 15) break;
                    }
                    if (g_stop || file_exists(cfg->pause_file)) break;
                    log_event(cfg, 0, "[GPU %d] External load finished -> active keepalive", cgpu.index);
                    last_nvml_check = now_ms();
                    continue;
                }
            }
            rc = cu.launch(fn, cfg->blocks, 1, 1, cfg->threads, 1, 1, 0, NULL, kparams, NULL);
            if (rc == 0) rc = cu.ctx_sync();
            if (rc != 0) {
                recoveries++;
                consecutive_failures++;
                log_event(cfg, 1, "[GPU %d] CUDA kernel error rc=%d, recovering (#%d)", cgpu.index, rc, recoveries);
                sleep_ms(1500);
                break;
            }
            double dt = now_ms() - t0;
            launches++;
            consecutive_failures = 0;

            if (launches > 5 && calib_count < 20) {
                base_ms = (base_ms * calib_count + dt) / (calib_count + 1);
                calib_count++;
            } else if (cfg->adaptive && calib_count >= 20) {
                if (!was_yielding && dt < base_ms) base_ms = base_ms * 0.9 + dt * 0.1;
                double thresh = base_ms * 1.85;
                if (base_ms + 0.85 > thresh) thresh = base_ms + 0.85;
                if (dt > thresh) {
                    yields++; idle_streak = 0;
                    if (!was_yielding) {
                        was_yielding = 1;
                        log_event(cfg, 0, "[GPU %d] Busy (%.2fms vs %.2fms) -> yield %dms", cgpu.index, dt, base_ms, cfg->yield_ms);
                    }
                    sleep_ms(cfg->yield_ms);
                    continue;
                } else if (was_yielding) {
                    idle_streak++;
                    if (idle_streak < 3) continue;
                    was_yielding = 0;
                    log_event(cfg, 0, "[GPU %d] Idle (%.2fms) -> active keepalive", cgpu.index, dt);
                }
            }
            if (cfg->idle_sleep_ms > 0) sleep_ms(cfg->idle_sleep_ms);
        }

        if (d_out) cu.mem_free(d_out);
        if (mod) cu.mod_unload(mod);
        if (ctx) cu.ctx_destroy(ctx);
    }

    log_event(cfg, 1, "[GPU %d] Stopped (CUDA): launches=%llu, yields=%llu, recoveries=%d, elapsed=%.1fs",
              cgpu.index, (unsigned long long)launches, (unsigned long long)yields, recoveries, (now_ms() - cfg->t_start) / 1000.0);
    return NULL;
}

/* ===================== Diagnostics ===================== */
static int run_check_vulkan(void) {
    if (!load_vulkan()) { printf("NO_LOADER\n"); return 2; }
    VkInstance inst = create_vk_instance();
    if (!inst) { printf("NO_NVIDIA_ICD\n"); return 3; }
    vk_gpu_t gpus[64];
    int n = enum_vk_nvidia(inst, gpus, 64);
    vk.destroy_inst(inst, NULL);
    if (n > 0) { printf("OK\t%d\n", n); return 0; }
    printf("NO_NVIDIA_ICD\n");
    return 3;
}

static int print_devices_tsv(void) {
    VkInstance inst = NULL;
    canonical_gpu_t gpus[64];
    int n = probe_canonical_gpus(&inst, gpus, 64);
    for (int i = 0; i < n; i++) {
        printf("%d\t%s\t%s\t0x%04X\t%llu\t%d\t%d\t%d\t%d\n",
               gpus[i].index,
               gpus[i].pci_bus_id[0] ? gpus[i].pci_bus_id : "-",
               gpus[i].name,
               gpus[i].device_id,
               (unsigned long long)gpus[i].vram_mib,
               gpus[i].is_mining,
               gpus[i].is_cmp40hx,
               gpus[i].has_vk,
               gpus[i].has_cuda);
    }
    if (inst) vk.destroy_inst(inst, NULL);
    return n > 0 ? 0 : 1;
}

static int print_devices(void) {
    VkInstance inst = NULL;
    canonical_gpu_t gpus[64];
    int n = probe_canonical_gpus(&inst, gpus, 64);
    if (n == 0) {
        fprintf(stderr, "No NVIDIA GPUs detected via Vulkan or CUDA.\n");
        if (inst) vk.destroy_inst(inst, NULL);
        return 1;
    }
    printf("Detected NVIDIA GPUs (sorted by PCI Bus ID):\n");
    for (int i = 0; i < n; i++) {
        printf("  [%d] %s [PCI=%s DEV=0x%04X] (%llu MiB)  Vulkan=%s  CUDA=%s%s\n",
               gpus[i].index, gpus[i].name,
               gpus[i].pci_bus_id[0] ? gpus[i].pci_bus_id : "n/a",
               gpus[i].device_id, (unsigned long long)gpus[i].vram_mib,
               gpus[i].has_vk ? "OK" : "NO", gpus[i].has_cuda ? "OK" : "NO",
               gpus[i].is_mining ? "  [mining]" : "");
    }
    if (inst) vk.destroy_inst(inst, NULL);
    return 0;
}

static int print_status(const config_t *cfg) {
    printf("=== NV-KeepAlive v%s Status (Linux) ===\n", NVK_VERSION);
    int running = 0;
    int fd = open("/tmp/nv-keepalive.lock", O_RDONLY);
    if (fd >= 0) {
        if (flock(fd, LOCK_EX | LOCK_NB) != 0) running = 1;
        else flock(fd, LOCK_UN);
        close(fd);
    }
    int vk_ok = 0;
    VkInstance inst = create_vk_instance();
    if (inst) {
        vk_gpu_t tmp[64];
        vk_ok = enum_vk_nvidia(inst, tmp, 64) > 0;
        vk.destroy_inst(inst, NULL);
    }
    printf("Service:         %s\n", running ? "RUNNING" : "NOT RUNNING");
    printf("Pause flag:      %s\n", file_exists(cfg->pause_file) ? cfg->pause_file : "inactive");
    printf("Vulkan Compute:  %s\n", vk_ok ? "AVAILABLE (~5-20 MiB VRAM)" : "UNAVAILABLE");
    if (!vk_ok) {
        printf("  To enable Vulkan mode, install the Vulkan loader:\n");
        printf("    Debian/Ubuntu: sudo apt install libvulkan1\n");
        printf("    Arch Linux:    sudo pacman -S vulkan-icd-loader\n");
        printf("    Fedora/RHEL:   sudo dnf install vulkan-loader\n");
    }
    print_devices();
    return 0;
}

static void print_help(const char *prog) {
    printf("NV-KeepAlive v%s (Linux - Vulkan Compute + CUDA Driver API)\n", NVK_VERSION);
    printf("Prevents idle voltage-drop TDRs and resets on NVIDIA GPUs.\n");
    printf("Usage: %s [options]\n", prog);
    printf("  --targets <spec>       Per-GPU backend list, e.g. '0:vulkan' or '0:cuda,1:vulkan'\n");
    printf("  --backend <mode>       'vulkan' (default) or 'cuda' (used when --targets is omitted)\n");
    printf("  --device <spec>        'auto' (mining cards, else GPU 0), 'all', or '0,1'\n");
    printf("  --list                 List detected NVIDIA GPUs (PCI Bus ID order)\n");
    printf("  --list-tsv             Machine-readable GPU list\n");
    printf("  --check-vulkan         Check Vulkan loader & NVIDIA ICD (OK / NO_LOADER / NO_NVIDIA_ICD)\n");
    printf("  --status               Show service status, Vulkan availability, and detected GPUs\n");
    printf("  --blocks <N>           Compute workgroups / grid blocks (default: 2)\n");
    printf("  --threads <N>          Threads per workgroup (default: 64)\n");
    printf("  --iters <N>            FMA iterations per launch (default: 450000 Vulkan / 120000 CUDA)\n");
    printf("  --sleep <ms>           Idle sleep ms between launches (default: 0)\n");
    printf("  --yield-ms <ms>        Adaptive sleep ms when GPU is busy (default: 150)\n");
    printf("  --no-adaptive          Disable adaptive yield during heavy GPU load\n");
    printf("  --pause-file <path>    Manual pause file (default: /run/nv-keepalive.pause)\n");
    printf("  --log <path>           Event-only log file (100 KB rotation)\n");
    printf("  --sec <N>              Run for N seconds and exit (0 = forever, default: 0)\n");
    printf("  --wait-timeout <N>     Seconds to wait for NVIDIA GPU at boot before exiting cleanly (default: 30)\n");
    printf("  --no-lock              Skip single-instance flock\n");
    printf("  --quiet                Minimal stdout output\n");
}

int main(int argc, char **argv) {
    config_t cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.backend = "vulkan";
    cfg.dev_spec = "auto";
    cfg.targets_spec = "";
    cfg.blocks = 2;
    cfg.threads = 64;
    cfg.yield_ms = 150;
    cfg.adaptive = 1;
    cfg.wait_timeout_sec = 30;
    cfg.use_lock = 1;
    cfg.pause_file = "/run/nv-keepalive.pause";
    cfg.log_file = "";
    cfg.t_start = now_ms();

    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-h") || !strcmp(argv[i], "--help")) { print_help(argv[0]); return 0; }
        else if (!strcmp(argv[i], "--list")) cfg.list_only = 1;
        else if (!strcmp(argv[i], "--list-tsv")) cfg.list_tsv = 1;
        else if (!strcmp(argv[i], "--status")) cfg.status_only = 1;
        else if (!strcmp(argv[i], "--check-vulkan")) cfg.check_vulkan = 1;
        else if (!strcmp(argv[i], "--targets") && i + 1 < argc) cfg.targets_spec = argv[++i];
        else if (!strcmp(argv[i], "--backend") && i + 1 < argc) cfg.backend = argv[++i];
        else if (!strcmp(argv[i], "--device") && i + 1 < argc) cfg.dev_spec = argv[++i];
        else if (!strcmp(argv[i], "--blocks") && i + 1 < argc) cfg.blocks = (unsigned int)strtoul(argv[++i], NULL, 10);
        else if (!strcmp(argv[i], "--threads") && i + 1 < argc) cfg.threads = (unsigned int)strtoul(argv[++i], NULL, 10);
        else if (!strcmp(argv[i], "--iters") && i + 1 < argc) cfg.iters = (uint32_t)strtoul(argv[++i], NULL, 10);
        else if (!strcmp(argv[i], "--sleep") && i + 1 < argc) cfg.idle_sleep_ms = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--yield-ms") && i + 1 < argc) cfg.yield_ms = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--no-adaptive")) cfg.adaptive = 0;
        else if (!strcmp(argv[i], "--pause-file") && i + 1 < argc) cfg.pause_file = argv[++i];
        else if (!strcmp(argv[i], "--log") && i + 1 < argc) cfg.log_file = argv[++i];
        else if (!strcmp(argv[i], "--sec") && i + 1 < argc) cfg.run_sec = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--wait-timeout") && i + 1 < argc) cfg.wait_timeout_sec = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--no-lock")) cfg.use_lock = 0;
        else if (!strcmp(argv[i], "--quiet")) cfg.quiet = 1;
    }

    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);

    if (cfg.check_vulkan) return run_check_vulkan();
    if (cfg.list_tsv) return print_devices_tsv();
    if (cfg.list_only) return print_devices();
    if (cfg.status_only) return print_status(&cfg);

    int lock_fd = -1;
    if (cfg.use_lock) {
        lock_fd = open("/tmp/nv-keepalive.lock", O_CREAT | O_RDWR, 0666);
        if (lock_fd >= 0 && flock(lock_fd, LOCK_EX | LOCK_NB) != 0) {
            log_event(&cfg, 0, "Already running (lock held). Exiting.");
            close(lock_fd);
            return 0;
        }
    }

    init_nvml_once();
    VkInstance vk_inst = NULL;
    canonical_gpu_t gpus[64];
    int gpu_count = 0;
    int logged_wait = 0, timed_out = 0;
    double t_wait = now_ms();

    while (!g_stop && (cfg.run_sec == 0 || (now_ms() - cfg.t_start) < (double)cfg.run_sec * 1000.0)) {
        if (vk_inst) { vk.destroy_inst(vk_inst, NULL); vk_inst = NULL; }
        gpu_count = probe_canonical_gpus(&vk_inst, gpus, 64);
        if (gpu_count > 0) break;
        if (cfg.wait_timeout_sec > 0 && (now_ms() - t_wait) >= (double)cfg.wait_timeout_sec * 1000.0) {
            timed_out = 1;
            break;
        }
        if (!logged_wait) {
            log_event(&cfg, 1, "Waiting for NVIDIA GPU/driver (timeout=%ds)...", cfg.wait_timeout_sec);
            logged_wait = 1;
        }
        sleep_ms(2000);
    }

    if (timed_out) {
        log_event(&cfg, 1, "No NVIDIA GPU detected after %ds - exiting cleanly.", cfg.wait_timeout_sec);
    } else if (!g_stop && gpu_count > 0) {
        worker_arg_t wa[64];
        int n_work = 0;
        uint32_t vk_iters = cfg.iters > 0 ? cfg.iters : 450000u;
        uint32_t cu_iters = cfg.iters > 0 ? cfg.iters : 120000u;

        if (cfg.targets_spec && cfg.targets_spec[0]) {
            char buf[512]; strncpy(buf, cfg.targets_spec, sizeof(buf) - 1); buf[sizeof(buf) - 1] = '\0';
            for (char *tok = strtok(buf, ",; "); tok && n_work < 64; tok = strtok(NULL, ",; ")) {
                char *col = strchr(tok, ':');
                int idx = atoi(tok);
                const char *b = col ? (col + 1) : cfg.backend;
                if (idx < 0 || idx >= gpu_count) continue;
                int use_vk = (strcasecmp(b, "cuda") != 0);
                if (use_vk && !gpus[idx].has_vk) {
                    log_event(&cfg, 1, "[GPU %d] Vulkan requested but unavailable on %s.", idx, gpus[idx].name);
                    continue;
                }
                if (!use_vk && !gpus[idx].has_cuda) {
                    log_event(&cfg, 1, "[GPU %d] CUDA requested but unavailable on %s.", idx, gpus[idx].name);
                    continue;
                }
                wa[n_work].gpu = gpus[idx];
                wa[n_work].use_vk = use_vk;
                wa[n_work].iters = use_vk ? vk_iters : cu_iters;
                wa[n_work].cfg = &cfg;
                n_work++;
            }
        } else {
            int targets[64], nt = 0;
            if (strcasecmp(cfg.dev_spec, "all") == 0) {
                for (int i = 0; i < gpu_count && nt < 64; i++) targets[nt++] = i;
            } else if (strcasecmp(cfg.dev_spec, "auto") == 0) {
                for (int i = 0; i < gpu_count && nt < 64; i++) if (gpus[i].is_mining) targets[nt++] = i;
                if (nt == 0) targets[nt++] = 0;
            } else {
                char buf[256]; strncpy(buf, cfg.dev_spec, sizeof(buf) - 1); buf[sizeof(buf) - 1] = '\0';
                for (char *tok = strtok(buf, ",; "); tok && nt < 64; tok = strtok(NULL, ",; ")) {
                    int idx = atoi(tok);
                    if (idx >= 0 && idx < gpu_count) targets[nt++] = idx;
                }
            }
            for (int i = 0; i < nt && n_work < 64; i++) {
                int idx = targets[i];
                int use_vk = (strcasecmp(cfg.backend, "cuda") != 0);
                if (strcasecmp(cfg.backend, "auto") == 0) use_vk = gpus[idx].has_vk;
                if (use_vk && !gpus[idx].has_vk) continue;
                if (!use_vk && !gpus[idx].has_cuda) continue;
                wa[n_work].gpu = gpus[idx];
                wa[n_work].use_vk = use_vk;
                wa[n_work].iters = use_vk ? vk_iters : cu_iters;
                wa[n_work].cfg = &cfg;
                n_work++;
            }
        }

        if (n_work > 0) {
            if (vk_inst) { vk.destroy_inst(vk_inst, NULL); vk_inst = NULL; }
            log_event(&cfg, 1, "NV-KeepAlive v%s started: workers=%d blocks=%u threads=%u yield=%dms",
                      NVK_VERSION, n_work, cfg.blocks, cfg.threads, cfg.yield_ms);
            if (n_work == 1) {
                if (wa[0].use_vk) vulkan_worker(&wa[0]);
                else cuda_worker(&wa[0]);
            } else {
                pthread_t th[64];
                for (int i = 0; i < n_work; i++) {
                    pthread_create(&th[i], NULL, wa[i].use_vk ? vulkan_worker : cuda_worker, &wa[i]);
                }
                for (int i = 0; i < n_work; i++) pthread_join(th[i], NULL);
            }
        } else {
            log_event(&cfg, 1, "No valid GPU/backend targets could be initialized.");
        }
    }

    if (vk_inst) vk.destroy_inst(vk_inst, NULL);
    if (nvml.ok && nvml.shutdown) nvml.shutdown();
    if (nvml.lib) dlclose(nvml.lib);
    if (lock_fd >= 0) close(lock_fd);
    if (cu.lib) dlclose(cu.lib);
    if (vk.lib) dlclose(vk.lib);

    if (g_need_restart && !g_shutdown) {
        sleep_ms(1500);
        execv("/proc/self/exe", argv);
    }
    return 0;
}
