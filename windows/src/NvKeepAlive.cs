using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;

// NV-KeepAlive v1.1.3 (Windows) - Minimalist, Zero-Dependency NVIDIA GPU KeepAlive
// Backends (compiled into a single binary, zero SDK dependencies):
//   1) Vulkan Compute (vulkan-1.dll + embedded SPIR-V 1.0) - ~5-20 MiB VRAM
//   2) CUDA Driver API (nvcuda.dll + embedded PTX sm_50+)  - ~200-300 MiB VRAM
// Supports per-GPU backend assignment (--targets 0:vulkan,1:cuda), NVML external-load yield,
// graceful Windows shutdown handling, and built-in PCIe chipset-bridge Gen2 retrain (--retrain-gen2).
class NvKeepAlive {
    const string Version = "1.1.3";
    const string MutexName = "Global\\NvKeepAliveMutex";
    const string RetrainMutexName = "Global\\NvKeepAliveGen2RetrainMutex";
    const long MaxLogBytes = 100 * 1024;

    // ===================== CUDA Driver API (nvcuda.dll) =====================
    [DllImport("nvcuda.dll")] static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetName(byte[] name, int len, int dev);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetPCIBusId(byte[] pciBusId, int len, int dev);
    [DllImport("nvcuda.dll")] static extern int cuDeviceGetLuid(byte[] luid, out uint deviceNodeMask, int dev);
    [DllImport("nvcuda.dll")] static extern int cuDeviceTotalMem_v2(out UIntPtr bytes, int dev);
    [DllImport("nvcuda.dll")] static extern int cuCtxCreate_v2(out IntPtr pctx, uint flags, int dev);
    [DllImport("nvcuda.dll")] static extern int cuCtxDestroy_v2(IntPtr ctx);
    [DllImport("nvcuda.dll")] static extern int cuCtxSynchronize();
    [DllImport("nvcuda.dll")] static extern int cuModuleLoadData(out IntPtr module, byte[] image);
    [DllImport("nvcuda.dll")] static extern int cuModuleUnload(IntPtr module);
    [DllImport("nvcuda.dll")] static extern int cuModuleGetFunction(out IntPtr hfunc, IntPtr hmod, string name);
    [DllImport("nvcuda.dll")] static extern int cuMemAlloc_v2(out ulong dptr, UIntPtr bytesize);
    [DllImport("nvcuda.dll")] static extern int cuMemFree_v2(ulong dptr);
    [DllImport("nvcuda.dll")] static extern int cuLaunchKernel(
        IntPtr f,
        uint gridDimX, uint gridDimY, uint gridDimZ,
        uint blockDimX, uint blockDimY, uint blockDimZ,
        uint sharedMemBytes, IntPtr hStream,
        IntPtr[] kernelParams, IntPtr[] extra);

    // ===================== Vulkan Compute API (vulkan-1.dll) =====================
    const uint VK_STRUCTURE_TYPE_APPLICATION_INFO = 0;
    const uint VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
    const uint VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2;
    const uint VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 3;
    const uint VK_STRUCTURE_TYPE_SUBMIT_INFO = 4;
    const uint VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 5;
    const uint VK_STRUCTURE_TYPE_FENCE_CREATE_INFO = 8;
    const uint VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO = 12;
    const uint VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO = 16;
    const uint VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO = 18;
    const uint VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO = 29;
    const uint VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO = 30;
    const uint VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO = 32;
    const uint VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO = 33;
    const uint VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO = 34;
    const uint VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET = 35;
    const uint VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39;
    const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40;
    const uint VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42;
    const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2 = 1000059001;
    const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES = 1000071004;
    const uint VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PCI_BUS_INFO_PROPERTIES_EXT = 1000212000;

    const uint VK_QUEUE_COMPUTE_BIT = 0x00000002;
    const uint VK_BUFFER_USAGE_STORAGE_BUFFER_BIT = 0x00000020;
    const uint VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x00000001;
    const uint VK_DESCRIPTOR_TYPE_STORAGE_BUFFER = 7;
    const uint VK_SHADER_STAGE_COMPUTE_BIT = 0x00000020;
    const uint VK_PIPELINE_BIND_POINT_COMPUTE = 1;

    [StructLayout(LayoutKind.Sequential)] struct VkApplicationInfo { public uint sType; public IntPtr pNext; public IntPtr pApplicationName; public uint applicationVersion; public IntPtr pEngineName; public uint engineVersion; public uint apiVersion; }
    [StructLayout(LayoutKind.Sequential)] struct VkInstanceCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public IntPtr pApplicationInfo; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; }
    [StructLayout(LayoutKind.Sequential)] struct VkQueueFamilyProperties { public uint queueFlags; public uint queueCount; public uint timestampValidBits; public uint minW, minH, minD; }
    [StructLayout(LayoutKind.Sequential)] struct VkDeviceQueueCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint queueFamilyIndex; public uint queueCount; public IntPtr pQueuePriorities; }
    [StructLayout(LayoutKind.Sequential)] struct VkDeviceCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint queueCreateInfoCount; public IntPtr pQueueCreateInfos; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; public IntPtr pEnabledFeatures; }
    [StructLayout(LayoutKind.Sequential)] struct VkBufferCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public ulong size; public uint usage; public uint sharingMode; public uint queueFamilyIndexCount; public IntPtr pQueueFamilyIndices; }
    [StructLayout(LayoutKind.Sequential)] struct VkMemoryRequirements { public ulong size; public ulong alignment; public uint memoryTypeBits; }
    [StructLayout(LayoutKind.Sequential)] struct VkMemoryAllocateInfo { public uint sType; public IntPtr pNext; public ulong allocationSize; public uint memoryTypeIndex; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorSetLayoutBinding { public uint binding; public uint descriptorType; public uint descriptorCount; public uint stageFlags; public IntPtr pImmutableSamplers; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorSetLayoutCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint bindingCount; public IntPtr pBindings; }
    [StructLayout(LayoutKind.Sequential)] struct VkPipelineLayoutCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint setLayoutCount; public IntPtr pSetLayouts; public uint pushConstantRangeCount; public IntPtr pPushConstantRanges; }
    [StructLayout(LayoutKind.Sequential)] struct VkShaderModuleCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public UIntPtr codeSize; public IntPtr pCode; }
    [StructLayout(LayoutKind.Sequential)] struct VkPipelineShaderStageCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint stage; public ulong module; public IntPtr pName; public IntPtr pSpecializationInfo; }
    [StructLayout(LayoutKind.Sequential)] struct VkComputePipelineCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public VkPipelineShaderStageCreateInfo stage; public ulong layout; public ulong basePipelineHandle; public int basePipelineIndex; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorPoolSize { public uint type; public uint descriptorCount; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorPoolCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint maxSets; public uint poolSizeCount; public IntPtr pPoolSizes; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorSetAllocateInfo { public uint sType; public IntPtr pNext; public ulong descriptorPool; public uint descriptorSetCount; public IntPtr pSetLayouts; }
    [StructLayout(LayoutKind.Sequential)] struct VkDescriptorBufferInfo { public ulong buffer; public ulong offset; public ulong range; }
    [StructLayout(LayoutKind.Sequential)] struct VkWriteDescriptorSet { public uint sType; public IntPtr pNext; public ulong dstSet; public uint dstBinding; public uint dstArrayElement; public uint descriptorCount; public uint descriptorType; public IntPtr pImageInfo; public IntPtr pBufferInfo; public IntPtr pTexelBufferView; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandPoolCreateInfo { public uint sType; public IntPtr pNext; public uint flags; public uint queueFamilyIndex; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandBufferAllocateInfo { public uint sType; public IntPtr pNext; public ulong commandPool; public uint level; public uint commandBufferCount; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandBufferBeginInfo { public uint sType; public IntPtr pNext; public uint flags; public IntPtr pInheritanceInfo; }
    [StructLayout(LayoutKind.Sequential)] struct VkSubmitInfo { public uint sType; public IntPtr pNext; public uint waitSemaphoreCount; public IntPtr pWaitSemaphores; public IntPtr pWaitDstStageMask; public uint commandBufferCount; public IntPtr pCommandBuffers; public uint signalSemaphoreCount; public IntPtr pSignalSemaphores; }
    [StructLayout(LayoutKind.Sequential)] struct VkFenceCreateInfo { public uint sType; public IntPtr pNext; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] struct VkPhysicalDevicePCIBusInfoPropertiesEXT { public uint sType; public IntPtr pNext; public uint pciDomain; public uint pciBus; public uint pciDevice; public uint pciFunction; }

    [DllImport("vulkan-1.dll")] static extern int vkCreateInstance(ref VkInstanceCreateInfo ci, IntPtr alloc, out IntPtr inst);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyInstance(IntPtr inst, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern IntPtr vkGetInstanceProcAddr(IntPtr inst, string pName);
    [DllImport("vulkan-1.dll")] static extern int vkEnumeratePhysicalDevices(IntPtr inst, ref uint count, IntPtr[] devs);
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceProperties(IntPtr pdev, byte[] props);
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr pdev, byte[] memProps);
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr pdev, ref uint count, [Out] VkQueueFamilyProperties[] props);
    [DllImport("vulkan-1.dll")] static extern int vkCreateDevice(IntPtr pdev, ref VkDeviceCreateInfo ci, IntPtr alloc, out IntPtr dev);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyDevice(IntPtr dev, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern void vkGetDeviceQueue(IntPtr dev, uint fam, uint idx, out IntPtr q);
    [DllImport("vulkan-1.dll")] static extern int vkCreateBuffer(IntPtr dev, ref VkBufferCreateInfo ci, IntPtr alloc, out ulong buf);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyBuffer(IntPtr dev, ulong buf, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern void vkGetBufferMemoryRequirements(IntPtr dev, ulong buf, out VkMemoryRequirements req);
    [DllImport("vulkan-1.dll")] static extern int vkAllocateMemory(IntPtr dev, ref VkMemoryAllocateInfo ai, IntPtr alloc, out ulong mem);
    [DllImport("vulkan-1.dll")] static extern void vkFreeMemory(IntPtr dev, ulong mem, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkBindBufferMemory(IntPtr dev, ulong buf, ulong mem, ulong off);
    [DllImport("vulkan-1.dll")] static extern int vkCreateDescriptorSetLayout(IntPtr dev, ref VkDescriptorSetLayoutCreateInfo ci, IntPtr alloc, out ulong layout);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyDescriptorSetLayout(IntPtr dev, ulong layout, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkCreatePipelineLayout(IntPtr dev, ref VkPipelineLayoutCreateInfo ci, IntPtr alloc, out ulong layout);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyPipelineLayout(IntPtr dev, ulong layout, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkCreateShaderModule(IntPtr dev, ref VkShaderModuleCreateInfo ci, IntPtr alloc, out ulong mod);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyShaderModule(IntPtr dev, ulong mod, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkCreateComputePipelines(IntPtr dev, ulong cache, uint count, ref VkComputePipelineCreateInfo ci, IntPtr alloc, out ulong pipe);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyPipeline(IntPtr dev, ulong pipe, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkCreateDescriptorPool(IntPtr dev, ref VkDescriptorPoolCreateInfo ci, IntPtr alloc, out ulong pool);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyDescriptorPool(IntPtr dev, ulong pool, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkAllocateDescriptorSets(IntPtr dev, ref VkDescriptorSetAllocateInfo ai, out ulong set);
    [DllImport("vulkan-1.dll")] static extern void vkUpdateDescriptorSets(IntPtr dev, uint wCount, ref VkWriteDescriptorSet w, uint cCount, IntPtr c);
    [DllImport("vulkan-1.dll")] static extern int vkCreateCommandPool(IntPtr dev, ref VkCommandPoolCreateInfo ci, IntPtr alloc, out ulong pool);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyCommandPool(IntPtr dev, ulong pool, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkAllocateCommandBuffers(IntPtr dev, ref VkCommandBufferAllocateInfo ai, out IntPtr cb);
    [DllImport("vulkan-1.dll")] static extern int vkBeginCommandBuffer(IntPtr cb, ref VkCommandBufferBeginInfo bi);
    [DllImport("vulkan-1.dll")] static extern int vkEndCommandBuffer(IntPtr cb);
    [DllImport("vulkan-1.dll")] static extern void vkCmdBindPipeline(IntPtr cb, uint bp, ulong pipe);
    [DllImport("vulkan-1.dll")] static extern void vkCmdBindDescriptorSets(IntPtr cb, uint bp, ulong layout, uint first, uint count, ref ulong set, uint dynCount, IntPtr dynOff);
    [DllImport("vulkan-1.dll")] static extern void vkCmdDispatch(IntPtr cb, uint x, uint y, uint z);
    [DllImport("vulkan-1.dll")] static extern int vkCreateFence(IntPtr dev, ref VkFenceCreateInfo ci, IntPtr alloc, out ulong fence);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyFence(IntPtr dev, ulong fence, IntPtr alloc);
    [DllImport("vulkan-1.dll")] static extern int vkResetFences(IntPtr dev, uint count, ref ulong fence);
    [DllImport("vulkan-1.dll")] static extern int vkWaitForFences(IntPtr dev, uint count, ref ulong fence, uint waitAll, ulong timeout);
    [DllImport("vulkan-1.dll")] static extern int vkQueueSubmit(IntPtr q, uint count, ref VkSubmitInfo si, ulong fence);
    [DllImport("vulkan-1.dll")] static extern int vkDeviceWaitIdle(IntPtr dev);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void FnVkGetPhysicalDeviceProperties2(IntPtr pdev, IntPtr pProps2);

    const string PtxSource =
        ".version 6.0\n" +
        ".target sm_50\n" +
        ".address_size 64\n" +
        ".visible .entry keepalive_kernel(.param .u64 out_ptr, .param .u32 iters)\n" +
        "{\n" +
        "    .reg .pred %p<2>;\n" +
        "    .reg .b32 %r<8>;\n" +
        "    .reg .f32 %f<4>;\n" +
        "    .reg .b64 %rd<4>;\n" +
        "    ld.param.u64 %rd1, [out_ptr];\n" +
        "    ld.param.u32 %r1, [iters];\n" +
        "    mov.u32 %r2, %ctaid.x;\n" +
        "    mov.u32 %r3, %ntid.x;\n" +
        "    mov.u32 %r4, %tid.x;\n" +
        "    mad.lo.s32 %r5, %r2, %r3, %r4;\n" +
        "    cvt.rn.f32.u32 %f1, %r5;\n" +
        "    mov.f32 %f2, 0f3F800001;\n" +
        "    mov.f32 %f3, 0f3B800000;\n" +
        "    mov.u32 %r6, 0;\n" +
        "loop:\n" +
        "    setp.ge.u32 %p1, %r6, %r1;\n" +
        "    @%p1 bra done;\n" +
        "    fma.rn.f32 %f1, %f1, %f2, %f3;\n" +
        "    fma.rn.f32 %f1, %f1, %f2, %f3;\n" +
        "    fma.rn.f32 %f1, %f1, %f2, %f3;\n" +
        "    fma.rn.f32 %f1, %f1, %f2, %f3;\n" +
        "    add.u32 %r6, %r6, 1;\n" +
        "    bra loop;\n" +
        "done:\n" +
        "    mul.wide.u32 %rd2, %r5, 4;\n" +
        "    add.s64 %rd3, %rd1, %rd2;\n" +
        "    st.global.f32 [%rd3], %f1;\n" +
        "    ret;\n" +
        "}\n";

    static readonly string[] MiningPatterns = new[] { "CMP", "P106", "P104", "P102" };
    static readonly uint[] MiningDeviceIds = new uint[] {
        0x1F0B, // CMP 40HX (TU106)
        0x2189, // CMP 30HX (TU116)
        0x1E09, // CMP 50HX (TU102)
        0x249D, // CMP 70HX (GA104)
        0x220D, 0x220A, // CMP 90HX (GA102)
        0x20B1, // CMP 170HX (GA100)
        0x1C07, 0x1C09, // P106-100 / P106-090
        0x1B87, // P104-100
        0x1B07  // P102-100
    };

    // ===================== Dynamic NVML (nvml.dll, optional) =====================
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeLibrary(IntPtr hModule);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    [StructLayout(LayoutKind.Sequential)] struct NvmlUtilization { public uint gpu; public uint memory; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNvmlGetHandle(uint index, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNvmlGetHandleByPci([MarshalAs(UnmanagedType.LPStr)] string pciBusId, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FnNvmlGetUtil(IntPtr dev, out NvmlUtilization util);

    static bool g_nvmlInited = false;
    static FnNvmlGetHandle g_nvmlGetHandle = null;
    static FnNvmlGetHandleByPci g_nvmlGetHandleByPci = null;
    static FnNvmlGetUtil g_nvmlGetUtil = null;
    static FnNvmlShutdown g_nvmlShutdown = null;

    static void InitNvmlOnce() {
        if (g_nvmlInited) return;
        g_nvmlInited = true;
        try {
            IntPtr h = LoadLibrary("nvml.dll");
            if (h == IntPtr.Zero) h = LoadLibrary(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll");
            if (h == IntPtr.Zero) return;
            IntPtr pInit = GetProcAddress(h, "nvmlInit_v2");
            IntPtr pShut = GetProcAddress(h, "nvmlShutdown");
            IntPtr pHand = GetProcAddress(h, "nvmlDeviceGetHandleByIndex_v2");
            IntPtr pPci  = GetProcAddress(h, "nvmlDeviceGetHandleByPciBusId_v2");
            IntPtr pUtil = GetProcAddress(h, "nvmlDeviceGetUtilizationRates");
            if (pInit == IntPtr.Zero || pHand == IntPtr.Zero || pUtil == IntPtr.Zero) return;
            var fnInit = (FnNvmlInit)Marshal.GetDelegateForFunctionPointer(pInit, typeof(FnNvmlInit));
            if (fnInit() != 0) return;
            g_nvmlGetHandle = (FnNvmlGetHandle)Marshal.GetDelegateForFunctionPointer(pHand, typeof(FnNvmlGetHandle));
            g_nvmlGetUtil   = (FnNvmlGetUtil)Marshal.GetDelegateForFunctionPointer(pUtil, typeof(FnNvmlGetUtil));
            if (pPci != IntPtr.Zero) g_nvmlGetHandleByPci = (FnNvmlGetHandleByPci)Marshal.GetDelegateForFunctionPointer(pPci, typeof(FnNvmlGetHandleByPci));
            if (pShut != IntPtr.Zero) g_nvmlShutdown = (FnNvmlShutdown)Marshal.GetDelegateForFunctionPointer(pShut, typeof(FnNvmlShutdown));
        } catch {}
    }

    static bool TryNvmlGetUtil(int nvIdx, string pciBusId, ref IntPtr nvmlDev, out uint gpuUtil, out uint memUtil) {
        gpuUtil = 0; memUtil = 0;
        if (g_nvmlGetUtil == null) return false;
        try {
            if (nvmlDev == IntPtr.Zero) {
                bool got = false;
                if (!string.IsNullOrEmpty(pciBusId) && g_nvmlGetHandleByPci != null) {
                    if (g_nvmlGetHandleByPci(pciBusId, out nvmlDev) == 0) got = true;
                }
                if (!got && g_nvmlGetHandle != null && g_nvmlGetHandle((uint)nvIdx, out nvmlDev) == 0) got = true;
                if (!got) return false;
            }
            NvmlUtilization u;
            if (g_nvmlGetUtil(nvmlDev, out u) != 0) { nvmlDev = IntPtr.Zero; return false; }
            gpuUtil = u.gpu; memUtil = u.memory;
            return true;
        } catch { nvmlDev = IntPtr.Zero; return false; }
    }

    // ===================== Win32 Graceful Shutdown Handler =====================
    delegate bool ConsoleCtrlDelegate(uint ctrlType);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    static ConsoleCtrlDelegate g_ctrlHandler = null;
    static readonly ManualResetEventSlim g_doneEvent = new ManualResetEventSlim(false);
    static Config g_activeCfg = null;

    static volatile bool g_stop = false;
    static volatile bool g_shutdown = false;
    static volatile bool g_needRestart = false;
    static readonly object g_logLock = new object();

    class Config {
        public string Backend = "vulkan"; // vulkan, cuda, auto
        public string DevSpec = "auto";
        public string TargetsSpec = "";   // e.g. "0:vulkan,1:cuda"
        public uint Blocks = 2;
        public uint Threads = 64;
        public uint Iters = 0;            // 0 = backend default (450000 vulkan, 120000 cuda)
        public int SleepMs = 0;
        public int YieldMs = 150;
        public bool Adaptive = true;
        public int RunSec = 0;
        public int WaitTimeoutSec = 30;
        public bool UseMutex = true;
        public bool Quiet = false;
        public bool ListOnly = false;
        public bool ListTsv = false;
        public bool StatusOnly = false;
        public bool CheckVulkan = false;
        public bool RetrainGen2 = false;
        public bool DryRun = false;
        public int RetrainWaitSec = 25;
        public string DriverPath = @"C:\ProgramData\40HXUnlock\drivers\WinRing0x64.sys";
        public string PauseFile = "";
        public string LogFile = "";
    }

    class VkGpuInfo {
        public IntPtr PDev;
        public string Name;
        public uint VendorId;
        public uint DeviceId;
        public ulong VramMiB;
        public uint ComputeQueueFamily;
        public string PciBusId; // "0000:04:00.0" or ""
        public string Luid;     // "luid_0x00000000_0x000139A8" or ""
    }

    class CudaGpuInfo {
        public int CudaOrdinal;
        public string Name;
        public ulong VramMiB;
        public string PciBusId; // "0000:04:00.0" or ""
        public string Luid;     // "luid_0x00000000_0x000139A8" or ""
    }

    class CanonicalGpu {
        public int Index;
        public string PciBusId;
        public string Luid;
        public string Name;
        public uint DeviceId;
        public ulong VramMiB;
        public VkGpuInfo Vk;
        public CudaGpuInfo Cuda;
        public bool IsMining;
        public bool IsCmp40hx;
    }

    struct TargetAssignment {
        public int CanonicalIndex;
        public string Backend; // "vulkan" or "cuda"
    }

    static string NormalizePciBusId(string raw) {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw.Trim().ToLowerInvariant();
        // Convert 8-char domain "00000000:04:00.0" -> 4-char "0000:04:00.0"
        if (s.Length == 16 && s[8] == ':') s = s.Substring(4);
        return s;
    }

    static void LogEvent(Config cfg, bool writeToFile, string fmt, params object[] args) {
        string msg = (args != null && args.Length > 0) ? string.Format(fmt, args) : fmt;
        string stamped = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, msg);
        lock (g_logLock) {
            if (!cfg.Quiet) Console.WriteLine(stamped);
            if (writeToFile && !string.IsNullOrEmpty(cfg.LogFile)) {
                try {
                    string dir = Path.GetDirectoryName(cfg.LogFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(cfg.LogFile) && new FileInfo(cfg.LogFile).Length > MaxLogBytes) {
                        string bak = cfg.LogFile + ".old";
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(cfg.LogFile, bak);
                    }
                    File.AppendAllText(cfg.LogFile, stamped + Environment.NewLine, Encoding.UTF8);
                } catch {}
            }
        }
    }

    static bool IsMiningCard(string name, uint deviceId) {
        if (deviceId != 0) {
            foreach (uint id in MiningDeviceIds) if (id == deviceId) return true;
        }
        if (!string.IsNullOrEmpty(name)) {
            foreach (string p in MiningPatterns)
                if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    static bool IsCmp40hxCard(string name, uint deviceId) {
        if (deviceId == 0x1F0B) return true;
        if (!string.IsNullOrEmpty(name) && name.IndexOf("CMP 40HX", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static bool IsExternalUnlockerActive() {
        try {
            var procs = Process.GetProcessesByName("40HXInstaller");
            bool active = procs != null && procs.Length > 0;
            if (procs != null) foreach (var p in procs) p.Dispose();
            return active;
        } catch { return false; }
    }

    static void ClearStalePauseOnStartup(Config cfg) {
        try {
            if (string.IsNullOrEmpty(cfg.PauseFile) || !File.Exists(cfg.PauseFile)) return;
            DateTime bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());
            DateTime writeUtc = File.GetLastWriteTimeUtc(cfg.PauseFile);
            if (writeUtc < bootUtc.AddSeconds(2)) {
                File.Delete(cfg.PauseFile);
                LogEvent(cfg, true, "Cleared stale pause file from previous boot.");
            }
        } catch {}
    }

    static bool IsPaused(Config cfg) {
        if (!string.IsNullOrEmpty(cfg.PauseFile) && File.Exists(cfg.PauseFile)) {
            try {
                string txt = File.ReadAllText(cfg.PauseFile).Trim();
                if (txt.Equals("gen2-sync", StringComparison.OrdinalIgnoreCase) || txt.Equals("gen2-retrain", StringComparison.OrdinalIgnoreCase)) {
                    double ageSec = (DateTime.UtcNow - File.GetLastWriteTimeUtc(cfg.PauseFile)).TotalSeconds;
                    if (ageSec > 75 && !IsExternalUnlockerActive()) {
                        File.Delete(cfg.PauseFile);
                        LogEvent(cfg, true, "Cleared orphaned {0} pause file (age={1:F0}s).", txt, ageSec);
                        return false;
                    }
                }
            } catch {}
            return true;
        }
        return false;
    }

    // ===================== SPIR-V 1.0 Builder (Vulkan Compute) =====================
    static void EmitSpv(List<uint> w, uint op, params uint[] args) {
        w.Add(((uint)(args.Length + 1) << 16) | op);
        for (int i = 0; i < args.Length; i++) w.Add(args[i]);
    }

    static uint FloatBits(float v) { return BitConverter.ToUInt32(BitConverter.GetBytes(v), 0); }

    static uint[] BuildSpirv(uint localSizeX, uint iters) {
        var w = new List<uint>();
        w.Add(0x07230203); // Magic
        w.Add(0x00010000); // SPIR-V 1.0
        w.Add(0);          // Generator
        w.Add(50);         // Bound
        w.Add(0);          // Schema

        const uint idMain = 1, idVoid = 2, idFnType = 3;
        const uint idU32 = 4, idF32 = 5, idBool = 6, idV3U32 = 7;
        const uint idPtrInV3U32 = 8, idPtrInU32 = 9, idGidVar = 10;
        const uint idRtArrF32 = 11, idBufStruct = 12, idPtrUniformBuf = 13, idBufVar = 14, idPtrUniformF32 = 15;
        const uint idC0 = 16, idC1 = 17, idCIters = 18, idCFMul = 19, idCFAdd = 20;
        const uint idPtrFnU32 = 21, idPtrFnF32 = 22;
        const uint idLblEntry = 23, idVarI = 24, idVarF = 25, idGidPtr = 26, idGidVal = 27, idFInit = 28;
        const uint idLblHead = 29, idLblBody = 30, idLblCont = 31, idLblMerge = 32;
        const uint idILoad = 33, idCmp = 34;
        const uint idFLoad = 35, idFM1 = 36, idFA1 = 37, idFM2 = 38, idFA2 = 39, idFM3 = 40, idFA3 = 41, idFM4 = 42, idFA4 = 43;
        const uint idINext = 44, idFFinal = 45, idOutElemPtr = 46;

        EmitSpv(w, 17, 1);
        EmitSpv(w, 14, 0, 1);
        EmitSpv(w, 15, 5, idMain, 0x6E69616Du, 0x00000000u, idGidVar);
        EmitSpv(w, 16, idMain, 17, localSizeX, 1, 1);

        EmitSpv(w, 71, idGidVar, 11, 28);
        EmitSpv(w, 71, idRtArrF32, 6, 4);
        EmitSpv(w, 72, idBufStruct, 0, 35, 0);
        EmitSpv(w, 71, idBufStruct, 3);
        EmitSpv(w, 71, idBufVar, 34, 0);
        EmitSpv(w, 71, idBufVar, 33, 0);

        EmitSpv(w, 19, idVoid);
        EmitSpv(w, 33, idFnType, idVoid);
        EmitSpv(w, 21, idU32, 32, 0);
        EmitSpv(w, 22, idF32, 32);
        EmitSpv(w, 20, idBool);
        EmitSpv(w, 23, idV3U32, idU32, 3);
        EmitSpv(w, 32, idPtrInV3U32, 1, idV3U32);
        EmitSpv(w, 32, idPtrInU32, 1, idU32);
        EmitSpv(w, 29, idRtArrF32, idF32);
        EmitSpv(w, 30, idBufStruct, idRtArrF32);
        EmitSpv(w, 32, idPtrUniformBuf, 2, idBufStruct);
        EmitSpv(w, 32, idPtrUniformF32, 2, idF32);
        EmitSpv(w, 32, idPtrFnU32, 7, idU32);
        EmitSpv(w, 32, idPtrFnF32, 7, idF32);

        EmitSpv(w, 43, idU32, idC0, 0);
        EmitSpv(w, 43, idU32, idC1, 1);
        EmitSpv(w, 43, idU32, idCIters, iters);
        EmitSpv(w, 43, idF32, idCFMul, FloatBits(1.000001f));
        EmitSpv(w, 43, idF32, idCFAdd, FloatBits(0.0009765625f));

        EmitSpv(w, 59, idPtrInV3U32, idGidVar, 1);
        EmitSpv(w, 59, idPtrUniformBuf, idBufVar, 2);

        EmitSpv(w, 54, idVoid, idMain, 0, idFnType);
        EmitSpv(w, 248, idLblEntry);
        EmitSpv(w, 59, idPtrFnU32, idVarI, 7);
        EmitSpv(w, 59, idPtrFnF32, idVarF, 7);
        EmitSpv(w, 65, idPtrInU32, idGidPtr, idGidVar, idC0);
        EmitSpv(w, 61, idU32, idGidVal, idGidPtr);
        EmitSpv(w, 112, idF32, idFInit, idGidVal);
        EmitSpv(w, 62, idVarF, idFInit);
        EmitSpv(w, 62, idVarI, idC0);
        EmitSpv(w, 249, idLblHead);

        EmitSpv(w, 248, idLblHead);
        EmitSpv(w, 246, idLblMerge, idLblCont, 0);
        EmitSpv(w, 61, idU32, idILoad, idVarI);
        EmitSpv(w, 176, idBool, idCmp, idILoad, idCIters);
        EmitSpv(w, 250, idCmp, idLblBody, idLblMerge);

        EmitSpv(w, 248, idLblBody);
        EmitSpv(w, 61, idF32, idFLoad, idVarF);
        EmitSpv(w, 133, idF32, idFM1, idFLoad, idCFMul); EmitSpv(w, 129, idF32, idFA1, idFM1, idCFAdd);
        EmitSpv(w, 133, idF32, idFM2, idFA1,   idCFMul); EmitSpv(w, 129, idF32, idFA2, idFM2, idCFAdd);
        EmitSpv(w, 133, idF32, idFM3, idFA2,   idCFMul); EmitSpv(w, 129, idF32, idFA3, idFM3, idCFAdd);
        EmitSpv(w, 133, idF32, idFM4, idFA3,   idCFMul); EmitSpv(w, 129, idF32, idFA4, idFM4, idCFAdd);
        EmitSpv(w, 62, idVarF, idFA4);
        EmitSpv(w, 249, idLblCont);

        EmitSpv(w, 248, idLblCont);
        EmitSpv(w, 128, idU32, idINext, idILoad, idC1);
        EmitSpv(w, 62, idVarI, idINext);
        EmitSpv(w, 249, idLblHead);

        EmitSpv(w, 248, idLblMerge);
        EmitSpv(w, 61, idF32, idFFinal, idVarF);
        EmitSpv(w, 65, idPtrUniformF32, idOutElemPtr, idBufVar, idC0, idGidVal);
        EmitSpv(w, 62, idOutElemPtr, idFFinal);
        EmitSpv(w, 253);
        EmitSpv(w, 56);
        return w.ToArray();
    }

    // ===================== Vulkan Enumeration & PCI Bus Info =====================
    static IntPtr CreateVkInstance() {
        IntPtr appName = Marshal.StringToHGlobalAnsi("NV-KeepAlive");
        // Request Vulkan 1.1 so vkGetPhysicalDeviceProperties2 is core (fallback to 1.0 if driver is ancient)
        uint[] vers = new uint[] { (1u << 22) | (1u << 12), (1u << 22) };
        IntPtr inst = IntPtr.Zero;
        foreach (uint ver in vers) {
            var ai = new VkApplicationInfo { sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, pApplicationName = appName, apiVersion = ver };
            IntPtr pAi = Marshal.AllocHGlobal(Marshal.SizeOf(ai));
            Marshal.StructureToPtr(ai, pAi, false);
            var ici = new VkInstanceCreateInfo { sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, pApplicationInfo = pAi };
            int rc = vkCreateInstance(ref ici, IntPtr.Zero, out inst);
            Marshal.FreeHGlobal(pAi);
            if (rc == 0 && inst != IntPtr.Zero) break;
            inst = IntPtr.Zero;
        }
        Marshal.FreeHGlobal(appName);
        return inst;
    }

    static string FormatLuidBytes(byte[] raw8, int offset) {
        uint low = BitConverter.ToUInt32(raw8, offset);
        uint high = BitConverter.ToUInt32(raw8, offset + 4);
        if (low == 0 && high == 0) return "";
        return string.Format("luid_0x{0:X8}_0x{1:X8}", high, low);
    }

    static void TryGetVkPciAndLuid(IntPtr inst, IntPtr pdev, out string pciBusId, out string luidStr) {
        pciBusId = "";
        luidStr = "";
        try {
            IntPtr pFn = vkGetInstanceProcAddr(inst, "vkGetPhysicalDeviceProperties2");
            if (pFn == IntPtr.Zero) pFn = vkGetInstanceProcAddr(inst, "vkGetPhysicalDeviceProperties2KHR");
            if (pFn == IntPtr.Zero) return;
            var fn = (FnVkGetPhysicalDeviceProperties2)Marshal.GetDelegateForFunctionPointer(pFn, typeof(FnVkGetPhysicalDeviceProperties2));

            // VkPhysicalDeviceIDProperties (x64): sType(0), pNext(8), deviceUUID(16..31), driverUUID(32..47), deviceLUID(48..55), nodeMask(56), luidValid(60)
            IntPtr pId = Marshal.AllocHGlobal(64);
            var pci = new VkPhysicalDevicePCIBusInfoPropertiesEXT {
                sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PCI_BUS_INFO_PROPERTIES_EXT,
                pNext = pId,
                pciDomain = 0xFFFFFFFFu
            };
            IntPtr pPci = Marshal.AllocHGlobal(Marshal.SizeOf(pci));
            IntPtr pProps2 = Marshal.AllocHGlobal(1024);
            try {
                byte[] idZeros = new byte[64];
                Marshal.Copy(idZeros, 0, pId, 64);
                Marshal.WriteInt32(pId, 0, unchecked((int)VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES));
                Marshal.WriteIntPtr(pId, IntPtr.Size, IntPtr.Zero);

                Marshal.StructureToPtr(pci, pPci, false);
                byte[] zeros = new byte[1024];
                Marshal.Copy(zeros, 0, pProps2, 1024);
                Marshal.WriteInt32(pProps2, 0, unchecked((int)VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2));
                Marshal.WriteIntPtr(pProps2, IntPtr.Size, pPci);

                fn(pdev, pProps2);

                pci = (VkPhysicalDevicePCIBusInfoPropertiesEXT)Marshal.PtrToStructure(pPci, typeof(VkPhysicalDevicePCIBusInfoPropertiesEXT));
                if (pci.pciDomain != 0xFFFFFFFFu && pci.pciBus <= 255 && pci.pciDevice <= 31 && pci.pciFunction <= 7) {
                    pciBusId = string.Format("{0:x4}:{1:x2}:{2:x2}.{3}", pci.pciDomain & 0xFFFF, pci.pciBus, pci.pciDevice, pci.pciFunction);
                }
                byte[] idOut = new byte[64];
                Marshal.Copy(pId, idOut, 0, 64);
                uint luidValid = BitConverter.ToUInt32(idOut, 60);
                if (luidValid != 0) luidStr = FormatLuidBytes(idOut, 48);
            } finally {
                Marshal.FreeHGlobal(pProps2);
                Marshal.FreeHGlobal(pPci);
                Marshal.FreeHGlobal(pId);
            }
        } catch {}
    }

    static List<VkGpuInfo> EnumerateVkNvidiaDevices(IntPtr inst) {
        var list = new List<VkGpuInfo>();
        if (inst == IntPtr.Zero) return list;
        uint pCount = 0;
        if (vkEnumeratePhysicalDevices(inst, ref pCount, null) != 0 || pCount == 0) return list;
        var pdevs = new IntPtr[pCount];
        if (vkEnumeratePhysicalDevices(inst, ref pCount, pdevs) != 0) return list;

        byte[] props = new byte[1024];
        byte[] memProps = new byte[1024];
        for (int i = 0; i < pCount; i++) {
            vkGetPhysicalDeviceProperties(pdevs[i], props);
            uint vendorId = BitConverter.ToUInt32(props, 8);
            if (vendorId != 0x10DE) continue; // NVIDIA only
            uint deviceId = BitConverter.ToUInt32(props, 12);
            int nameEnd = Array.IndexOf(props, (byte)0, 20, 256);
            string dName = Encoding.ASCII.GetString(props, 20, (nameEnd < 0 ? 256 : nameEnd - 20)).Trim();

            ulong vramBytes = 0;
            vkGetPhysicalDeviceMemoryProperties(pdevs[i], memProps);
            uint heapCount = BitConverter.ToUInt32(memProps, 4 + 32 * 8);
            for (uint h = 0; h < heapCount && h < 16; h++) {
                int off = 4 + 32 * 8 + 4 + (int)h * 16;
                ulong hSize = BitConverter.ToUInt64(memProps, off);
                uint hFlags = BitConverter.ToUInt32(memProps, off + 8);
                if ((hFlags & 1u) != 0 && hSize > vramBytes) vramBytes = hSize;
            }

            uint qCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(pdevs[i], ref qCount, null);
            var qProps = new VkQueueFamilyProperties[qCount];
            vkGetPhysicalDeviceQueueFamilyProperties(pdevs[i], ref qCount, qProps);
            uint qFam = uint.MaxValue;
            for (uint q = 0; q < qCount; q++) {
                if ((qProps[q].queueFlags & VK_QUEUE_COMPUTE_BIT) != 0) { qFam = q; break; }
            }
            if (qFam == uint.MaxValue) continue;

            string vkPci, vkLuid;
            TryGetVkPciAndLuid(inst, pdevs[i], out vkPci, out vkLuid);
            list.Add(new VkGpuInfo {
                PDev = pdevs[i],
                Name = dName,
                VendorId = vendorId,
                DeviceId = deviceId,
                VramMiB = vramBytes / (1024UL * 1024UL),
                ComputeQueueFamily = qFam,
                PciBusId = vkPci,
                Luid = vkLuid
            });
        }
        return list;
    }

    static string GetCudaDeviceName(int dev) {
        byte[] buf = new byte[256];
        if (cuDeviceGetName(buf, buf.Length, dev) != 0) return "Unknown";
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, len).Trim();
    }

    static string GetCudaPciBusId(int dev) {
        try {
            byte[] buf = new byte[64];
            if (cuDeviceGetPCIBusId(buf, buf.Length, dev) != 0) return "";
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return NormalizePciBusId(Encoding.ASCII.GetString(buf, 0, len));
        } catch { return ""; }
    }

    static string GetCudaLuid(int dev) {
        try {
            byte[] buf = new byte[8];
            uint mask;
            if (cuDeviceGetLuid(buf, out mask, dev) != 0) return "";
            return FormatLuidBytes(buf, 0);
        } catch { return ""; }
    }

    static List<CudaGpuInfo> EnumerateCudaDevices() {
        var list = new List<CudaGpuInfo>();
        try {
            int count;
            if (cuInit(0) != 0 || cuDeviceGetCount(out count) != 0 || count <= 0) return list;
            for (int i = 0; i < count; i++) {
                int d;
                if (cuDeviceGet(out d, i) != 0) continue;
                UIntPtr mem;
                ulong mb = (cuDeviceTotalMem_v2(out mem, d) == 0) ? (mem.ToUInt64() / (1024UL * 1024UL)) : 0;
                list.Add(new CudaGpuInfo {
                    CudaOrdinal = i,
                    Name = GetCudaDeviceName(d),
                    VramMiB = mb,
                    PciBusId = GetCudaPciBusId(d),
                    Luid = GetCudaLuid(d)
                });
            }
        } catch {}
        return list;
    }

    // Merge Vulkan and CUDA device lists into a single canonical PCI-sorted GPU list
    static List<CanonicalGpu> BuildCanonicalGpus(List<VkGpuInfo> vkGpus, List<CudaGpuInfo> cudaGpus) {
        var list = new List<CanonicalGpu>();
        var usedCuda = new HashSet<int>();

        if (vkGpus != null) {
            foreach (var v in vkGpus) {
                var cg = new CanonicalGpu {
                    PciBusId = v.PciBusId,
                    Luid = v.Luid,
                    Name = v.Name,
                    DeviceId = v.DeviceId,
                    VramMiB = v.VramMiB,
                    Vk = v
                };
                if (cudaGpus != null) {
                    CudaGpuInfo match = null;
                    if (!string.IsNullOrEmpty(v.PciBusId)) {
                        foreach (var c in cudaGpus) {
                            if (!usedCuda.Contains(c.CudaOrdinal) && string.Equals(c.PciBusId, v.PciBusId, StringComparison.OrdinalIgnoreCase)) {
                                match = c; break;
                            }
                        }
                    }
                    if (match == null) {
                        foreach (var c in cudaGpus) {
                            if (!usedCuda.Contains(c.CudaOrdinal) && string.Equals(c.Name, v.Name, StringComparison.OrdinalIgnoreCase)) {
                                match = c; break;
                            }
                        }
                    }
                    if (match != null) {
                        cg.Cuda = match;
                        usedCuda.Add(match.CudaOrdinal);
                        if (string.IsNullOrEmpty(cg.PciBusId)) cg.PciBusId = match.PciBusId;
                        if (string.IsNullOrEmpty(cg.Luid)) cg.Luid = match.Luid;
                    }
                }
                list.Add(cg);
            }
        }

        if (cudaGpus != null) {
            foreach (var c in cudaGpus) {
                if (usedCuda.Contains(c.CudaOrdinal)) continue;
                list.Add(new CanonicalGpu {
                    PciBusId = c.PciBusId,
                    Luid = c.Luid,
                    Name = c.Name,
                    DeviceId = 0,
                    VramMiB = c.VramMiB,
                    Cuda = c
                });
            }
        }

        list.Sort((a, b) => {
            bool ha = !string.IsNullOrEmpty(a.PciBusId);
            bool hb = !string.IsNullOrEmpty(b.PciBusId);
            if (ha && hb) return string.Compare(a.PciBusId, b.PciBusId, StringComparison.OrdinalIgnoreCase);
            if (ha) return -1;
            if (hb) return 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        for (int i = 0; i < list.Count; i++) {
            list[i].Index = i;
            list[i].IsMining = IsMiningCard(list[i].Name, list[i].DeviceId);
            list[i].IsCmp40hx = IsCmp40hxCard(list[i].Name, list[i].DeviceId);
        }
        return list;
    }

    // ===================== Workers (Vulkan & CUDA) =====================
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    static void RunVulkanWorker(CanonicalGpu cgpu, Config cfg, uint iters, Stopwatch totalSw) {
        long launches = 0, yields = 0;
        int recoveries = 0, consecutiveFailures = 0;
        uint[] spirv = BuildSpirv(cfg.Threads, iters);

        while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
            if (IsPaused(cfg)) {
                LogEvent(cfg, true, "[GPU {0}] Paused - Vulkan context released.", cgpu.Index);
                while (!g_stop && IsPaused(cfg) && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                    Thread.Sleep(500);
                }
                if (g_stop || (cfg.RunSec > 0 && totalSw.Elapsed.TotalSeconds >= cfg.RunSec)) break;
                LogEvent(cfg, true, "[GPU {0}] Pause cleared, resuming.", cgpu.Index);
                Thread.Sleep(800);
                if (g_stop || IsPaused(cfg)) continue;
            }

            IntPtr localInst = IntPtr.Zero;
            IntPtr dev = IntPtr.Zero;
            ulong buf = 0, mem = 0, dsl = 0, pl = 0, sm = 0, pipe = 0, dp = 0, ds = 0, cp = 0, fence = 0;
            GCHandle hCb = default(GCHandle);
            try {
                // Always create a fresh VkInstance & re-enumerate VkPhysicalDevice per cycle
                // so a PCIe Gen2 reset (40HXInstaller) or WDDM TDR never leaves a stale PDev pointer.
                localInst = CreateVkInstance();
                if (localInst == IntPtr.Zero) throw new Exception("vkCreateInstance failed");
                var freshGpus = EnumerateVkNvidiaDevices(localInst);
                if (freshGpus == null || freshGpus.Count == 0) throw new Exception("No NVIDIA Vulkan GPU after reset");
                VkGpuInfo gpu = null;
                if (!string.IsNullOrEmpty(cgpu.PciBusId)) {
                    foreach (var fg in freshGpus) {
                        if (string.Equals(fg.PciBusId, cgpu.PciBusId, StringComparison.OrdinalIgnoreCase)) { gpu = fg; break; }
                    }
                }
                if (gpu == null && cgpu.DeviceId != 0) {
                    foreach (var fg in freshGpus) {
                        if (fg.DeviceId == cgpu.DeviceId) { gpu = fg; break; }
                    }
                }
                if (gpu == null) gpu = (cgpu.Index >= 0 && cgpu.Index < freshGpus.Count) ? freshGpus[cgpu.Index] : freshGpus[0];

                float[] prio = new float[] { 1.0f };
                GCHandle hPrio = GCHandle.Alloc(prio, GCHandleType.Pinned);
                var qci = new VkDeviceQueueCreateInfo {
                    sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                    queueFamilyIndex = gpu.ComputeQueueFamily, queueCount = 1, pQueuePriorities = hPrio.AddrOfPinnedObject()
                };
                IntPtr pQci = Marshal.AllocHGlobal(Marshal.SizeOf(qci));
                Marshal.StructureToPtr(qci, pQci, false);
                var dci = new VkDeviceCreateInfo { sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, queueCreateInfoCount = 1, pQueueCreateInfos = pQci };
                int rc;
                try {
                    rc = vkCreateDevice(gpu.PDev, ref dci, IntPtr.Zero, out dev);
                } finally {
                    Marshal.FreeHGlobal(pQci); hPrio.Free();
                }
                if (rc != 0) throw new Exception("vkCreateDevice=" + rc);

                IntPtr queue;
                vkGetDeviceQueue(dev, gpu.ComputeQueueFamily, 0, out queue);

                ulong bufBytes = (ulong)cfg.Blocks * cfg.Threads * 4UL;
                var bci = new VkBufferCreateInfo { sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, size = bufBytes, usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT };
                if ((rc = vkCreateBuffer(dev, ref bci, IntPtr.Zero, out buf)) != 0) throw new Exception("vkCreateBuffer=" + rc);
                VkMemoryRequirements req;
                vkGetBufferMemoryRequirements(dev, buf, out req);

                byte[] memProps = new byte[1024];
                vkGetPhysicalDeviceMemoryProperties(gpu.PDev, memProps);
                uint memTypeCount = BitConverter.ToUInt32(memProps, 0);
                uint memTypeIdx = 0;
                for (uint i = 0; i < memTypeCount; i++) {
                    uint mFlags = BitConverter.ToUInt32(memProps, 4 + (int)i * 8);
                    if ((req.memoryTypeBits & (1u << (int)i)) != 0 && (mFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) != 0) { memTypeIdx = i; break; }
                }
                var mai = new VkMemoryAllocateInfo { sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, allocationSize = req.size, memoryTypeIndex = memTypeIdx };
                if ((rc = vkAllocateMemory(dev, ref mai, IntPtr.Zero, out mem)) != 0) throw new Exception("vkAllocateMemory=" + rc);
                if ((rc = vkBindBufferMemory(dev, buf, mem, 0)) != 0) throw new Exception("vkBindBufferMemory=" + rc);

                var bind = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, descriptorCount = 1, stageFlags = VK_SHADER_STAGE_COMPUTE_BIT };
                IntPtr pBind = Marshal.AllocHGlobal(Marshal.SizeOf(bind)); Marshal.StructureToPtr(bind, pBind, false);
                var dslci = new VkDescriptorSetLayoutCreateInfo { sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, bindingCount = 1, pBindings = pBind };
                rc = vkCreateDescriptorSetLayout(dev, ref dslci, IntPtr.Zero, out dsl);
                Marshal.FreeHGlobal(pBind);
                if (rc != 0) throw new Exception("vkCreateDescriptorSetLayout=" + rc);

                ulong[] dslArr = new[] { dsl };
                GCHandle hDsl = GCHandle.Alloc(dslArr, GCHandleType.Pinned);
                try {
                    var plci = new VkPipelineLayoutCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, setLayoutCount = 1, pSetLayouts = hDsl.AddrOfPinnedObject() };
                    if ((rc = vkCreatePipelineLayout(dev, ref plci, IntPtr.Zero, out pl)) != 0) throw new Exception("vkCreatePipelineLayout=" + rc);

                    GCHandle hSpv = GCHandle.Alloc(spirv, GCHandleType.Pinned);
                    try {
                        var smci = new VkShaderModuleCreateInfo { sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, codeSize = (UIntPtr)(spirv.Length * 4), pCode = hSpv.AddrOfPinnedObject() };
                        if ((rc = vkCreateShaderModule(dev, ref smci, IntPtr.Zero, out sm)) != 0) throw new Exception("vkCreateShaderModule=" + rc);
                    } finally { hSpv.Free(); }

                    IntPtr entryName = Marshal.StringToHGlobalAnsi("main");
                    var cpci = new VkComputePipelineCreateInfo {
                        sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
                        stage = new VkPipelineShaderStageCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, stage = VK_SHADER_STAGE_COMPUTE_BIT, module = sm, pName = entryName },
                        layout = pl, basePipelineIndex = -1
                    };
                    rc = vkCreateComputePipelines(dev, 0, 1, ref cpci, IntPtr.Zero, out pipe);
                    Marshal.FreeHGlobal(entryName);
                    if (rc != 0) throw new Exception("vkCreateComputePipelines=" + rc);

                    var dps = new VkDescriptorPoolSize { type = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, descriptorCount = 1 };
                    IntPtr pDps = Marshal.AllocHGlobal(Marshal.SizeOf(dps)); Marshal.StructureToPtr(dps, pDps, false);
                    var dpci = new VkDescriptorPoolCreateInfo { sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO, maxSets = 1, poolSizeCount = 1, pPoolSizes = pDps };
                    rc = vkCreateDescriptorPool(dev, ref dpci, IntPtr.Zero, out dp);
                    Marshal.FreeHGlobal(pDps);
                    if (rc != 0) throw new Exception("vkCreateDescriptorPool=" + rc);

                    var dsai = new VkDescriptorSetAllocateInfo { sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO, descriptorPool = dp, descriptorSetCount = 1, pSetLayouts = hDsl.AddrOfPinnedObject() };
                    if ((rc = vkAllocateDescriptorSets(dev, ref dsai, out ds)) != 0) throw new Exception("vkAllocateDescriptorSets=" + rc);
                } finally { hDsl.Free(); }

                var dbi = new VkDescriptorBufferInfo { buffer = buf, offset = 0, range = bufBytes };
                IntPtr pDbi = Marshal.AllocHGlobal(Marshal.SizeOf(dbi)); Marshal.StructureToPtr(dbi, pDbi, false);
                var wds = new VkWriteDescriptorSet { sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, dstSet = ds, dstBinding = 0, descriptorCount = 1, descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, pBufferInfo = pDbi };
                vkUpdateDescriptorSets(dev, 1, ref wds, 0, IntPtr.Zero);
                Marshal.FreeHGlobal(pDbi);

                var cpCreate = new VkCommandPoolCreateInfo { sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, queueFamilyIndex = gpu.ComputeQueueFamily };
                if ((rc = vkCreateCommandPool(dev, ref cpCreate, IntPtr.Zero, out cp)) != 0) throw new Exception("vkCreateCommandPool=" + rc);
                var cbai = new VkCommandBufferAllocateInfo { sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, commandPool = cp, level = 0, commandBufferCount = 1 };
                IntPtr cb;
                if ((rc = vkAllocateCommandBuffers(dev, ref cbai, out cb)) != 0) throw new Exception("vkAllocateCommandBuffers=" + rc);

                var cbbi = new VkCommandBufferBeginInfo { sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
                if ((rc = vkBeginCommandBuffer(cb, ref cbbi)) != 0) throw new Exception("vkBeginCommandBuffer=" + rc);
                vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_COMPUTE, pipe);
                vkCmdBindDescriptorSets(cb, VK_PIPELINE_BIND_POINT_COMPUTE, pl, 0, 1, ref ds, 0, IntPtr.Zero);
                vkCmdDispatch(cb, cfg.Blocks, 1, 1);
                if ((rc = vkEndCommandBuffer(cb)) != 0) throw new Exception("vkEndCommandBuffer=" + rc);

                var fci = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
                if ((rc = vkCreateFence(dev, ref fci, IntPtr.Zero, out fence)) != 0) throw new Exception("vkCreateFence=" + rc);

                IntPtr[] cbArr = new[] { cb };
                hCb = GCHandle.Alloc(cbArr, GCHandleType.Pinned);
                var si = new VkSubmitInfo { sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, commandBufferCount = 1, pCommandBuffers = hCb.AddrOfPinnedObject() };

                LogEvent(cfg, true, "[GPU {0}] Active (Vulkan): {1} [DEV=0x{2:X4}] (PCI {3}, buf={4} B, iters={5})",
                    cgpu.Index, cgpu.Name, cgpu.DeviceId, string.IsNullOrEmpty(cgpu.PciBusId) ? "n/a" : cgpu.PciBusId, bufBytes, iters);

                double baseMs = 0.0;
                int calibCount = 0, idleStreak = 0;
                bool wasYielding = false;
                var kSw = new Stopwatch();
                var pauseCheckSw = Stopwatch.StartNew();
                var nvmlCheckSw = Stopwatch.StartNew();
                IntPtr nvmlDev = IntPtr.Zero;

                while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                    if (pauseCheckSw.ElapsedMilliseconds >= 300) {
                        pauseCheckSw.Restart();
                        if (IsPaused(cfg)) break;
                    }

                    if (cfg.Adaptive && nvmlCheckSw.ElapsedMilliseconds >= 250) {
                        nvmlCheckSw.Restart();
                        uint uGpu, uMem;
                        if (TryNvmlGetUtil(cgpu.Index, cgpu.PciBusId, ref nvmlDev, out uGpu, out uMem) && uMem >= 5) {
                            yields++;
                            LogEvent(cfg, false, "[GPU {0}] External load (mem={1}%, gpu={2}%) -> yielding completely", cgpu.Index, uMem, uGpu);
                            while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                                Thread.Sleep(250);
                                if (pauseCheckSw.ElapsedMilliseconds >= 300) {
                                    pauseCheckSw.Restart();
                                    if (IsPaused(cfg)) break;
                                }
                                if (!TryNvmlGetUtil(cgpu.Index, cgpu.PciBusId, ref nvmlDev, out uGpu, out uMem)) break;
                                if (uMem < 5 && uGpu < 15) break;
                            }
                            if (IsPaused(cfg) || g_stop) break;
                            LogEvent(cfg, false, "[GPU {0}] External load finished -> active keepalive", cgpu.Index);
                            nvmlCheckSw.Restart();
                            continue;
                        }
                    }

                    kSw.Restart();
                    if ((rc = vkResetFences(dev, 1, ref fence)) != 0) throw new Exception("vkResetFences=" + rc);
                    if ((rc = vkQueueSubmit(queue, 1, ref si, fence)) != 0) throw new Exception("vkQueueSubmit=" + rc);
                    if ((rc = vkWaitForFences(dev, 1, ref fence, 1, 5000000000UL)) != 0) throw new Exception("vkWaitForFences=" + rc);
                    kSw.Stop();
                    launches++;
                    consecutiveFailures = 0;

                    double ms = kSw.Elapsed.TotalMilliseconds;
                    if (launches > 5 && calibCount < 20) {
                        baseMs = (baseMs * calibCount + ms) / (calibCount + 1);
                        calibCount++;
                    } else if (cfg.Adaptive && calibCount >= 20) {
                        if (!wasYielding && ms < baseMs) baseMs = baseMs * 0.9 + ms * 0.1;
                        double busyThresh = Math.Max(baseMs * 1.85, baseMs + 0.85);
                        if (ms > busyThresh) {
                            yields++;
                            idleStreak = 0;
                            if (!wasYielding) {
                                wasYielding = true;
                                LogEvent(cfg, false, "[GPU {0}] Busy ({1:F2}ms vs {2:F2}ms) -> yield {3}ms", cgpu.Index, ms, baseMs, cfg.YieldMs);
                            }
                            Thread.Sleep(cfg.YieldMs);
                            continue;
                        } else if (wasYielding) {
                            idleStreak++;
                            if (idleStreak < 3) continue;
                            wasYielding = false;
                            LogEvent(cfg, false, "[GPU {0}] Idle ({1:F2}ms) -> active keepalive", cgpu.Index, ms);
                        }
                    }
                    if (cfg.SleepMs > 0) Thread.Sleep(cfg.SleepMs);
                }
            } catch (Exception ex) {
                recoveries++;
                consecutiveFailures++;
                bool fatalFault = (ex is AccessViolationException);
                LogEvent(cfg, true, "[GPU {0}] Vulkan recovering (#{1}): {2}", cgpu.Index, recoveries, ex.Message);
                if ((fatalFault || consecutiveFailures >= 3) && cfg.RunSec == 0) {
                    LogEvent(cfg, true, "[GPU {0}] Vulkan handle lost after TDR/reset ({1}) - restarting fresh process...", cgpu.Index, ex.Message);
                    g_needRestart = true;
                    g_stop = true;
                } else {
                    Thread.Sleep(1500);
                }
            } finally {
                if (hCb.IsAllocated) hCb.Free();
                if (dev != IntPtr.Zero) {
                    try { vkDeviceWaitIdle(dev); } catch {}
                    try { if (fence != 0) vkDestroyFence(dev, fence, IntPtr.Zero); } catch {}
                    try { if (cp != 0) vkDestroyCommandPool(dev, cp, IntPtr.Zero); } catch {}
                    try { if (dp != 0) vkDestroyDescriptorPool(dev, dp, IntPtr.Zero); } catch {}
                    try { if (pipe != 0) vkDestroyPipeline(dev, pipe, IntPtr.Zero); } catch {}
                    try { if (sm != 0) vkDestroyShaderModule(dev, sm, IntPtr.Zero); } catch {}
                    try { if (pl != 0) vkDestroyPipelineLayout(dev, pl, IntPtr.Zero); } catch {}
                    try { if (dsl != 0) vkDestroyDescriptorSetLayout(dev, dsl, IntPtr.Zero); } catch {}
                    try { if (buf != 0) vkDestroyBuffer(dev, buf, IntPtr.Zero); } catch {}
                    try { if (mem != 0) vkFreeMemory(dev, mem, IntPtr.Zero); } catch {}
                    try { vkDestroyDevice(dev, IntPtr.Zero); } catch {}
                }
                if (localInst != IntPtr.Zero) {
                    try { vkDestroyInstance(localInst, IntPtr.Zero); } catch {}
                }
            }
        }

        LogEvent(cfg, true, "[GPU {0}] Stopped (Vulkan): launches={1}, yields={2}, recoveries={3}, elapsed={4:F1}s",
            cgpu.Index, launches, yields, recoveries, totalSw.Elapsed.TotalSeconds);
    }

    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    static void RunCudaWorker(CanonicalGpu cgpu, Config cfg, uint iters, byte[] ptxBytes, Stopwatch totalSw) {
        int ordinal = cgpu.Cuda.CudaOrdinal;
        long launches = 0, yields = 0;
        int recoveries = 0, consecutiveFailures = 0;

        while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
            if (IsPaused(cfg)) {
                LogEvent(cfg, true, "[GPU {0}] Paused - CUDA context released.", cgpu.Index);
                while (!g_stop && IsPaused(cfg) && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                    Thread.Sleep(500);
                }
                if (g_stop || (cfg.RunSec > 0 && totalSw.Elapsed.TotalSeconds >= cfg.RunSec)) break;
                LogEvent(cfg, true, "[GPU {0}] Pause cleared, resuming.", cgpu.Index);
            }

            IntPtr ctx = IntPtr.Zero, mod = IntPtr.Zero;
            ulong dOut = 0;
            GCHandle hPtr = default(GCHandle), hIters = default(GCHandle);

            try {
                int dev, rc;
                if ((rc = cuDeviceGet(out dev, ordinal)) != 0) throw new Exception("cuDeviceGet=" + rc);
                if ((rc = cuCtxCreate_v2(out ctx, 4, dev)) != 0) throw new Exception("cuCtxCreate=" + rc);
                if ((rc = cuModuleLoadData(out mod, ptxBytes)) != 0) throw new Exception("cuModuleLoadData=" + rc);
                IntPtr fn;
                if ((rc = cuModuleGetFunction(out fn, mod, "keepalive_kernel")) != 0) throw new Exception("cuModuleGetFunction=" + rc);
                UIntPtr bufBytes = (UIntPtr)((ulong)cfg.Blocks * cfg.Threads * 4UL);
                if ((rc = cuMemAlloc_v2(out dOut, bufBytes)) != 0) throw new Exception("cuMemAlloc=" + rc);

                LogEvent(cfg, true, "[GPU {0}] Active (CUDA): {1} (PCI {2}, buf={3} B, iters={4})",
                    cgpu.Index, cgpu.Name, string.IsNullOrEmpty(cgpu.PciBusId) ? "n/a" : cgpu.PciBusId, bufBytes, iters);

                ulong[] ptrBox = new ulong[] { dOut };
                uint[] itersBox = new uint[] { iters };
                hPtr = GCHandle.Alloc(ptrBox, GCHandleType.Pinned);
                hIters = GCHandle.Alloc(itersBox, GCHandleType.Pinned);
                IntPtr[] kParams = new IntPtr[] { hPtr.AddrOfPinnedObject(), hIters.AddrOfPinnedObject() };

                double baseMs = 0.0;
                int calibCount = 0, idleStreak = 0;
                bool wasYielding = false;
                var kSw = new Stopwatch();
                var pauseCheckSw = Stopwatch.StartNew();
                var nvmlCheckSw = Stopwatch.StartNew();
                IntPtr nvmlDev = IntPtr.Zero;

                while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                    if (pauseCheckSw.ElapsedMilliseconds >= 300) {
                        pauseCheckSw.Restart();
                        if (IsPaused(cfg)) break;
                    }

                    if (cfg.Adaptive && nvmlCheckSw.ElapsedMilliseconds >= 250) {
                        nvmlCheckSw.Restart();
                        uint uGpu, uMem;
                        if (TryNvmlGetUtil(cgpu.Index, cgpu.PciBusId, ref nvmlDev, out uGpu, out uMem) && uMem >= 5) {
                            yields++;
                            LogEvent(cfg, false, "[GPU {0}] External load (mem={1}%, gpu={2}%) -> yielding completely", cgpu.Index, uMem, uGpu);
                            while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                                Thread.Sleep(250);
                                if (pauseCheckSw.ElapsedMilliseconds >= 300) {
                                    pauseCheckSw.Restart();
                                    if (IsPaused(cfg)) break;
                                }
                                if (!TryNvmlGetUtil(cgpu.Index, cgpu.PciBusId, ref nvmlDev, out uGpu, out uMem)) break;
                                if (uMem < 5 && uGpu < 15) break;
                            }
                            if (IsPaused(cfg) || g_stop) break;
                            LogEvent(cfg, false, "[GPU {0}] External load finished -> active keepalive", cgpu.Index);
                            nvmlCheckSw.Restart();
                            continue;
                        }
                    }

                    kSw.Restart();
                    rc = cuLaunchKernel(fn, cfg.Blocks, 1, 1, cfg.Threads, 1, 1, 0, IntPtr.Zero, kParams, null);
                    if (rc != 0) throw new Exception("cuLaunchKernel=" + rc);
                    rc = cuCtxSynchronize();
                    if (rc != 0) throw new Exception("cuCtxSynchronize=" + rc);
                    kSw.Stop();
                    launches++;
                    consecutiveFailures = 0;

                    double ms = kSw.Elapsed.TotalMilliseconds;
                    if (launches > 5 && calibCount < 20) {
                        baseMs = (baseMs * calibCount + ms) / (calibCount + 1);
                        calibCount++;
                    } else if (cfg.Adaptive && calibCount >= 20) {
                        if (!wasYielding && ms < baseMs) baseMs = baseMs * 0.9 + ms * 0.1;
                        double busyThresh = Math.Max(baseMs * 1.85, baseMs + 0.85);
                        if (ms > busyThresh) {
                            yields++;
                            idleStreak = 0;
                            if (!wasYielding) {
                                wasYielding = true;
                                LogEvent(cfg, false, "[GPU {0}] Busy ({1:F2}ms vs {2:F2}ms) -> yield {3}ms", cgpu.Index, ms, baseMs, cfg.YieldMs);
                            }
                            Thread.Sleep(cfg.YieldMs);
                            continue;
                        } else if (wasYielding) {
                            idleStreak++;
                            if (idleStreak < 3) continue;
                            wasYielding = false;
                            LogEvent(cfg, false, "[GPU {0}] Idle ({1:F2}ms) -> active keepalive", cgpu.Index, ms);
                        }
                    }
                    if (cfg.SleepMs > 0) Thread.Sleep(cfg.SleepMs);
                }
            } catch (Exception ex) {
                recoveries++;
                consecutiveFailures++;
                bool fatalFault = (ex is AccessViolationException);
                LogEvent(cfg, true, "[GPU {0}] CUDA recovering (#{1}): {2}", cgpu.Index, recoveries, ex.Message);
                if ((fatalFault || consecutiveFailures >= 3) && cfg.RunSec == 0) {
                    LogEvent(cfg, true, "[GPU {0}] CUDA driver handle poisoned after TDR/reset ({1}) - restarting fresh process...", cgpu.Index, ex.Message);
                    g_needRestart = true;
                    g_stop = true;
                } else {
                    Thread.Sleep(1500);
                }
            } finally {
                if (hPtr.IsAllocated) hPtr.Free();
                if (hIters.IsAllocated) hIters.Free();
                try { if (dOut != 0) cuMemFree_v2(dOut); } catch {}
                try { if (mod != IntPtr.Zero) cuModuleUnload(mod); } catch {}
                try { if (ctx != IntPtr.Zero) cuCtxDestroy_v2(ctx); } catch {}
            }
        }

        LogEvent(cfg, true, "[GPU {0}] Stopped (CUDA): launches={1}, yields={2}, recoveries={3}, elapsed={4:F1}s",
            cgpu.Index, launches, yields, recoveries, totalSw.Elapsed.TotalSeconds);
    }

    // ===================== Built-in PCIe Gen2 Chipset-Bridge Retrain (--retrain-gen2) =====================
    const uint GENERIC_READ  = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ  = 1;
    const uint FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    const uint SERVICE_ALL_ACCESS    = 0xF01FF;
    const uint SERVICE_KERNEL_DRIVER = 0x00000001;
    const uint SERVICE_DEMAND_START  = 0x00000003;
    const uint SERVICE_ERROR_NORMAL  = 0x00000001;
    const uint SERVICE_CONTROL_STOP  = 0x00000001;
    const uint IOCTL_OLS_READ_PCI_CONFIG  = 0x9C406144;
    const uint IOCTL_OLS_WRITE_PCI_CONFIG = 0x9C40A148;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(IntPtr hService);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr hSCObject);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);

    static uint MakePciAddr(int bus, int dev, int func) { return (uint)(((bus & 0xFF) << 8) | ((dev & 0x1F) << 3) | (func & 7)); }
    static bool ReadPciDword(IntPtr hDev, int b, int d, int f, int off, out uint val) {
        val = 0xFFFFFFFFu;
        byte[] inp = new byte[8]; BitConverter.GetBytes(MakePciAddr(b, d, f)).CopyTo(inp, 0); BitConverter.GetBytes((uint)off).CopyTo(inp, 4);
        byte[] outp = new byte[4]; uint br;
        if (!DeviceIoControl(hDev, IOCTL_OLS_READ_PCI_CONFIG, inp, 8, outp, 4, out br, IntPtr.Zero) || br < 4) return false;
        val = BitConverter.ToUInt32(outp, 0); return true;
    }
    static bool ReadPciWord(IntPtr hDev, int b, int d, int f, int off, out ushort val) {
        val = 0xFFFF;
        byte[] inp = new byte[8]; BitConverter.GetBytes(MakePciAddr(b, d, f)).CopyTo(inp, 0); BitConverter.GetBytes((uint)off).CopyTo(inp, 4);
        byte[] outp = new byte[2]; uint br;
        if (!DeviceIoControl(hDev, IOCTL_OLS_READ_PCI_CONFIG, inp, 8, outp, 2, out br, IntPtr.Zero) || br < 2) return false;
        val = BitConverter.ToUInt16(outp, 0); return true;
    }
    static bool ReadPciByte(IntPtr hDev, int b, int d, int f, int off, out byte val) {
        val = 0xFF;
        byte[] inp = new byte[8]; BitConverter.GetBytes(MakePciAddr(b, d, f)).CopyTo(inp, 0); BitConverter.GetBytes((uint)off).CopyTo(inp, 4);
        byte[] outp = new byte[1]; uint br;
        if (!DeviceIoControl(hDev, IOCTL_OLS_READ_PCI_CONFIG, inp, 8, outp, 1, out br, IntPtr.Zero) || br < 1) return false;
        val = outp[0]; return true;
    }
    static bool WritePciWord(IntPtr hDev, int b, int d, int f, int off, ushort val) {
        byte[] inp = new byte[10]; BitConverter.GetBytes(MakePciAddr(b, d, f)).CopyTo(inp, 0); BitConverter.GetBytes((uint)off).CopyTo(inp, 4); BitConverter.GetBytes(val).CopyTo(inp, 8);
        uint br; return DeviceIoControl(hDev, IOCTL_OLS_WRITE_PCI_CONFIG, inp, 10, null, 0, out br, IntPtr.Zero);
    }
    static int FindPcieCap(IntPtr hDev, int b, int d, int f) {
        ushort status; if (!ReadPciWord(hDev, b, d, f, 0x06, out status) || (status & 0x0010) == 0) return -1;
        byte ptr; if (!ReadPciByte(hDev, b, d, f, 0x34, out ptr)) return -1;
        int off = ptr & 0xFC, guard = 0;
        while (off >= 0x40 && off < 0xFC && guard++ < 48) {
            byte id, next;
            if (!ReadPciByte(hDev, b, d, f, off, out id) || !ReadPciByte(hDev, b, d, f, off + 1, out next)) return -1;
            if (id == 0x10) return off;
            off = next & 0xFC;
        }
        return -1;
    }
    struct PcieLinkInfo { public int CurSpd, CurWid, MaxSpd, MaxWid, Tls; public ushort LnkCtl, LnkCtl2; }
    static PcieLinkInfo ReadLinkInfo(IntPtr hDev, int b, int d, int f, int cap) {
        uint lnkcap = 0; ushort lnkctl = 0, lnksta = 0, lnkctl2 = 0;
        ReadPciDword(hDev, b, d, f, cap + 0x0C, out lnkcap);
        ReadPciWord(hDev, b, d, f, cap + 0x10, out lnkctl);
        ReadPciWord(hDev, b, d, f, cap + 0x12, out lnksta);
        ReadPciWord(hDev, b, d, f, cap + 0x30, out lnkctl2);
        return new PcieLinkInfo {
            MaxSpd = (int)(lnkcap & 0xF), MaxWid = (int)((lnkcap >> 4) & 0x3F),
            CurSpd = lnksta & 0xF, CurWid = (lnksta >> 4) & 0x3F,
            Tls = lnkctl2 & 0xF, LnkCtl = lnkctl, LnkCtl2 = lnkctl2
        };
    }

    class WinRing0Session : IDisposable {
        IntPtr _hScm = IntPtr.Zero, _hSvc = IntPtr.Zero;
        public IntPtr Handle = new IntPtr(-1);
        bool _createdSvc = false;
        bool _startedSvc = false;
        public bool Open(string drvPath) {
            Handle = CreateFile(@"\\.\WinRing0_1_2_0", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (Handle != new IntPtr(-1)) return true;
            _hScm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (_hScm == IntPtr.Zero) return false;
            _hSvc = OpenService(_hScm, "WinRing0_1_2_0", SERVICE_ALL_ACCESS);
            if (_hSvc == IntPtr.Zero) {
                _hSvc = CreateService(_hScm, "WinRing0_1_2_0", "WinRing0_1_2_0", SERVICE_ALL_ACCESS,
                    SERVICE_KERNEL_DRIVER, SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL, drvPath, null, IntPtr.Zero, null, null, null);
                if (_hSvc == IntPtr.Zero) return false;
                _createdSvc = true;
            }
            if (StartService(_hSvc, 0, IntPtr.Zero)) {
                _startedSvc = true;
            } else if (Marshal.GetLastWin32Error() != 1056) {
                return false;
            }
            Handle = CreateFile(@"\\.\WinRing0_1_2_0", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            return Handle != new IntPtr(-1);
        }
        public void Dispose() {
            if (Handle != new IntPtr(-1) && Handle != IntPtr.Zero) { CloseHandle(Handle); Handle = new IntPtr(-1); }
            if (_hSvc != IntPtr.Zero) {
                if (_startedSvc || _createdSvc) {
                    SERVICE_STATUS ss; ControlService(_hSvc, SERVICE_CONTROL_STOP, out ss);
                    if (_createdSvc) DeleteService(_hSvc);
                }
                CloseServiceHandle(_hSvc); _hSvc = IntPtr.Zero;
            }
            if (_hScm != IntPtr.Zero) { CloseServiceHandle(_hScm); _hScm = IntPtr.Zero; }
        }
    }

    struct PciDevPair { public int gB, gD, gF, bB, bD, bF; public uint bVidDid; }

    static List<PciDevPair> FindCmp40hxAndBridges(IntPtr hDev) {
        var gpus = new List<int[]>();
        for (int b = 0; b < 256; b++) {
            for (int d = 0; d < 32; d++) {
                uint id0; if (!ReadPciDword(hDev, b, d, 0, 0x00, out id0) || (id0 & 0xFFFF) == 0xFFFF) continue;
                byte hdr; ReadPciByte(hDev, b, d, 0, 0x0E, out hdr);
                int maxF = ((hdr & 0x80) != 0) ? 8 : 1;
                for (int f = 0; f < maxF; f++) {
                    uint id;
                    if (ReadPciDword(hDev, b, d, f, 0x00, out id) && (id & 0xFFFF) == 0x10DE && ((id >> 16) & 0xFFFF) == 0x1F0B) {
                        gpus.Add(new[] { b, d, f });
                    }
                }
            }
        }
        var pairs = new List<PciDevPair>();
        foreach (var g in gpus) {
            int brB = -1, brD = -1, brF = -1; uint brId = 0;
            for (int b = 0; b < 256 && brB < 0; b++) {
                for (int d = 0; d < 32 && brB < 0; d++) {
                    uint id0; if (!ReadPciDword(hDev, b, d, 0, 0x00, out id0) || (id0 & 0xFFFF) == 0xFFFF) continue;
                    byte hdr0; ReadPciByte(hDev, b, d, 0, 0x0E, out hdr0);
                    int maxF = ((hdr0 & 0x80) != 0) ? 8 : 1;
                    for (int f = 0; f < maxF; f++) {
                        uint id; byte hdr, secBus;
                        if (!ReadPciDword(hDev, b, d, f, 0x00, out id) || (id & 0xFFFF) == 0xFFFF) continue;
                        if (!ReadPciByte(hDev, b, d, f, 0x0E, out hdr) || (hdr & 0x7F) != 1) continue;
                        if (!ReadPciByte(hDev, b, d, f, 0x19, out secBus)) continue;
                        if (secBus == g[0]) { brB = b; brD = d; brF = f; brId = id; break; }
                    }
                }
            }
            if (brB >= 0) pairs.Add(new PciDevPair { gB = g[0], gD = g[1], gF = g[2], bB = brB, bD = brD, bF = brF, bVidDid = brId });
        }
        return pairs;
    }

    static int RunRetrainGen2(Config cfg) {
        if (!File.Exists(cfg.DriverPath)) {
            LogEvent(cfg, true, "[Gen2-Retrain] WinRing0 driver not found ({0}). Install CMP40HX-Unlock first.", cfg.DriverPath);
            return 2;
        }
        bool created;
        using (var mtx = new Mutex(true, RetrainMutexName, out created)) {
            if (!created) return 0;
            var sw = Stopwatch.StartNew();
            while (true) {
                bool anyPending = false;
                using (var wr = new WinRing0Session()) {
                    if (!wr.Open(cfg.DriverPath)) {
                        if (!cfg.DryRun && sw.Elapsed.TotalSeconds < cfg.RetrainWaitSec) {
                            Thread.Sleep(400);
                            continue;
                        }
                        LogEvent(cfg, true, "[Gen2-Retrain] Cannot open WinRing0 driver (Administrator/SYSTEM privileges required).");
                        return 3;
                    }
                    var pairs = FindCmp40hxAndBridges(wr.Handle);
                    if (pairs.Count == 0) {
                        LogEvent(cfg, true, "[Gen2-Retrain] No CMP 40HX (10DE:1F0B) detected.");
                        return 0;
                    }
                    foreach (var p in pairs) {
                        int gCap = FindPcieCap(wr.Handle, p.gB, p.gD, p.gF);
                        int bCap = FindPcieCap(wr.Handle, p.bB, p.bD, p.bF);
                        if (gCap < 0 || bCap < 0) continue;
                        var gi = ReadLinkInfo(wr.Handle, p.gB, p.gD, p.gF, gCap);
                        var bi = ReadLinkInfo(wr.Handle, p.bB, p.bD, p.bF, bCap);

                        if (cfg.DryRun) {
                            LogEvent(cfg, false, "[Gen2-Retrain] CMP 40HX {0:X2}:{1:X2}.{2} cur=Gen{3} x{4} max=Gen{5} TLS=Gen{6} | Bridge {7:X2}:{8:X2}.{9} ({10:X4}:{11:X4}) cur=Gen{12} x{13} max=Gen{14} TLS=Gen{15}",
                                p.gB, p.gD, p.gF, gi.CurSpd, gi.CurWid, gi.MaxSpd, gi.Tls,
                                p.bB, p.bD, p.bF, p.bVidDid & 0xFFFF, (p.bVidDid >> 16) & 0xFFFF, bi.CurSpd, bi.CurWid, bi.MaxSpd, bi.Tls);
                            continue;
                        }
                        if (gi.CurSpd >= 2 && bi.CurSpd >= 2) {
                            LogEvent(cfg, true, "[Gen2-Retrain] {0:X2}:{1:X2}.{2} <-> Bridge {3:X2}:{4:X2}.{5} already at Gen{6} x{7}.",
                                p.gB, p.gD, p.gF, p.bB, p.bD, p.bF, gi.CurSpd, gi.CurWid);
                            continue;
                        }
                        if (gi.MaxSpd >= 2 && bi.MaxSpd >= 2) {
                            if (gi.Tls != 2) WritePciWord(wr.Handle, p.gB, p.gD, p.gF, gCap + 0x30, (ushort)((gi.LnkCtl2 & 0xFFF0) | 2));
                            WritePciWord(wr.Handle, p.bB, p.bD, p.bF, bCap + 0x30, (ushort)((bi.LnkCtl2 & 0xFFF0) | 2));
                            WritePciWord(wr.Handle, p.bB, p.bD, p.bF, bCap + 0x10, (ushort)(bi.LnkCtl | 0x0020));
                            for (int t = 0; t < 40; t++) {
                                Thread.Sleep(25);
                                ushort st;
                                if (ReadPciWord(wr.Handle, p.bB, p.bD, p.bF, bCap + 0x12, out st) && (st & 0x0800) == 0) break;
                            }
                            Thread.Sleep(100);
                            var ga = ReadLinkInfo(wr.Handle, p.gB, p.gD, p.gF, gCap);
                            var ba = ReadLinkInfo(wr.Handle, p.bB, p.bD, p.bF, bCap);
                            LogEvent(cfg, true, "[Gen2-Retrain] Retrained {0:X2}:{1:X2}.{2} <-> Bridge {3:X2}:{4:X2}.{5} ({6:X4}:{7:X4}): Gen{8} x{9} -> Gen{10} x{11} (Bridge Gen{12} x{13})",
                                p.gB, p.gD, p.gF, p.bB, p.bD, p.bF, p.bVidDid & 0xFFFF, (p.bVidDid >> 16) & 0xFFFF,
                                gi.CurSpd, gi.CurWid, ga.CurSpd, ga.CurWid, ba.CurSpd, ba.CurWid);
                        } else {
                            anyPending = true;
                        }
                    }
                }
                if (cfg.DryRun || !anyPending) return 0;
                if (sw.Elapsed.TotalSeconds >= cfg.RetrainWaitSec) {
                    LogEvent(cfg, true, "[Gen2-Retrain] GPU still reports max=Gen1 after {0}s (40HXInstaller -gen2 has not unlocked link capability yet).", cfg.RetrainWaitSec);
                    return 0;
                }
                Thread.Sleep(400);
            }
        }
    }

    // ===================== Diagnostics (--check-vulkan / --list-tsv / --list / --status) =====================
    static int CheckVulkanDetailed(out int nvCount) {
        nvCount = 0;
        IntPtr hLib = LoadLibrary("vulkan-1.dll");
        if (hLib == IntPtr.Zero) return 2; // NO_LOADER
        FreeLibrary(hLib);
        try {
            IntPtr inst = CreateVkInstance();
            if (inst == IntPtr.Zero) return 3; // NO_NVIDIA_ICD
            try {
                var devs = EnumerateVkNvidiaDevices(inst);
                nvCount = devs.Count;
                return nvCount > 0 ? 0 : 3;
            } finally {
                vkDestroyInstance(inst, IntPtr.Zero);
            }
        } catch {
            return 3;
        }
    }

    static int RunCheckVulkan() {
        int n;
        int rc = CheckVulkanDetailed(out n);
        if (rc == 0) Console.WriteLine("OK\t" + n);
        else if (rc == 2) Console.WriteLine("NO_LOADER");
        else Console.WriteLine("NO_NVIDIA_ICD");
        return rc;
    }

    static List<CanonicalGpu> ProbeAllGpus(out IntPtr vkInst) {
        vkInst = IntPtr.Zero;
        List<VkGpuInfo> vkGpus = null;
        try {
            IntPtr hLib = LoadLibrary("vulkan-1.dll");
            if (hLib != IntPtr.Zero) {
                FreeLibrary(hLib);
                vkInst = CreateVkInstance();
                if (vkInst != IntPtr.Zero) vkGpus = EnumerateVkNvidiaDevices(vkInst);
            }
        } catch {}
        List<CudaGpuInfo> cudaGpus = EnumerateCudaDevices();
        return BuildCanonicalGpus(vkGpus, cudaGpus);
    }

    static int PrintDevicesTsv() {
        IntPtr vkInst;
        var gpus = ProbeAllGpus(out vkInst);
        try {
            foreach (var g in gpus) {
                Console.WriteLine("{0}\t{1}\t{2}\t0x{3:X4}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}",
                    g.Index,
                    string.IsNullOrEmpty(g.PciBusId) ? "-" : g.PciBusId,
                    g.Name.Replace('\t', ' '),
                    g.DeviceId,
                    g.VramMiB,
                    g.IsMining ? 1 : 0,
                    g.IsCmp40hx ? 1 : 0,
                    g.Vk != null ? 1 : 0,
                    g.Cuda != null ? 1 : 0,
                    string.IsNullOrEmpty(g.Luid) ? "-" : g.Luid);
            }
            return gpus.Count > 0 ? 0 : 1;
        } finally {
            if (vkInst != IntPtr.Zero) { try { vkDestroyInstance(vkInst, IntPtr.Zero); } catch {} }
        }
    }

    static int PrintDevices() {
        IntPtr vkInst;
        var gpus = ProbeAllGpus(out vkInst);
        try {
            if (gpus.Count == 0) {
                Console.Error.WriteLine("No NVIDIA GPUs detected via Vulkan or CUDA.");
                return 1;
            }
            Console.WriteLine("Detected NVIDIA GPUs (sorted by PCI Bus ID):");
            foreach (var g in gpus) {
                Console.WriteLine("  [{0}] {1} [PCI={2} DEV=0x{3:X4}] ({4} MiB)  Vulkan={5}  CUDA={6}{7}",
                    g.Index, g.Name,
                    string.IsNullOrEmpty(g.PciBusId) ? "n/a" : g.PciBusId,
                    g.DeviceId, g.VramMiB,
                    g.Vk != null ? "OK" : "NO",
                    g.Cuda != null ? "OK" : "NO",
                    g.IsMining ? "  [mining]" : "");
            }
            return 0;
        } finally {
            if (vkInst != IntPtr.Zero) { try { vkDestroyInstance(vkInst, IntPtr.Zero); } catch {} }
        }
    }

    static int PrintStatus(Config cfg) {
        Console.WriteLine("=== NV-KeepAlive v" + Version + " Status ===");
        bool running = false;
        try {
            Mutex m;
            if (Mutex.TryOpenExisting(MutexName, out m)) { running = true; m.Dispose(); }
        } catch (UnauthorizedAccessException) { running = true; } catch {}

        int vkN;
        int vkRc = CheckVulkanDetailed(out vkN);
        string progDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NV-KeepAlive");
        string pausePath = File.Exists(cfg.PauseFile) ? cfg.PauseFile : Path.Combine(progDataDir, "keepalive.pause");
        bool paused = File.Exists(pausePath);

        string vkEnvM = Environment.GetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", EnvironmentVariableTarget.Machine);
        string vkEnvU = Environment.GetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", EnvironmentVariableTarget.User);
        string vkEnv  = !string.IsNullOrEmpty(vkEnvM) ? vkEnvM : (!string.IsNullOrEmpty(vkEnvU) ? vkEnvU : "not set");

        Console.WriteLine("Service:                   " + (running ? "RUNNING" : "NOT RUNNING"));
        Console.WriteLine("Pause flag:                " + (paused ? ("PAUSED (" + pausePath + ")") : "inactive"));
        Console.WriteLine("Vulkan Compute:            " + (vkRc == 0 ? ("AVAILABLE (" + vkN + " NVIDIA GPU)") : (vkRc == 2 ? "MISSING (vulkan-1.dll not found)" : "UNAVAILABLE (no NVIDIA Vulkan ICD)")));
        Console.WriteLine("GGML_VK_DISABLE_COOPMAT2:  " + vkEnv);
        PrintDevices();

        string logPath = !string.IsNullOrEmpty(cfg.LogFile) ? cfg.LogFile : Path.Combine(progDataDir, "keepalive.log");
        if (File.Exists(logPath)) {
            Console.WriteLine("Recent log (" + logPath + "):");
            string[] lines = File.ReadAllLines(logPath, Encoding.UTF8);
            int start = Math.Max(0, lines.Length - 10);
            for (int i = start; i < lines.Length; i++) Console.WriteLine("  " + lines[i]);
        }
        return 0;
    }

    static List<TargetAssignment> ResolveAssignments(Config cfg, List<CanonicalGpu> gpus) {
        var res = new List<TargetAssignment>();
        if (!string.IsNullOrEmpty(cfg.TargetsSpec)) {
            foreach (string rawTok in cfg.TargetsSpec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                string tok = rawTok.Trim();
                int colon = tok.IndexOf(':');
                int idx;
                string b = cfg.Backend;
                if (colon > 0) {
                    if (!int.TryParse(tok.Substring(0, colon), out idx)) continue;
                    b = tok.Substring(colon + 1).Trim().ToLowerInvariant();
                } else {
                    if (!int.TryParse(tok, out idx)) continue;
                }
                if (idx < 0 || idx >= gpus.Count) continue;
                if (b != "vulkan" && b != "cuda") b = (gpus[idx].Vk != null) ? "vulkan" : "cuda";
                res.Add(new TargetAssignment { CanonicalIndex = idx, Backend = b });
            }
            return res;
        }

        // Legacy --backend + --device mode
        var indices = new List<int>();
        if (string.Equals(cfg.DevSpec, "all", StringComparison.OrdinalIgnoreCase)) {
            for (int i = 0; i < gpus.Count; i++) indices.Add(i);
        } else if (string.Equals(cfg.DevSpec, "auto", StringComparison.OrdinalIgnoreCase)) {
            for (int i = 0; i < gpus.Count; i++) if (gpus[i].IsMining) indices.Add(i);
            if (indices.Count == 0 && gpus.Count > 0) indices.Add(0);
        } else {
            foreach (string tok in cfg.DevSpec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                int idx;
                if (int.TryParse(tok.Trim(), out idx) && idx >= 0 && idx < gpus.Count && !indices.Contains(idx)) indices.Add(idx);
            }
        }

        foreach (int idx in indices) {
            string b = cfg.Backend;
            if (b == "auto") b = (gpus[idx].Vk != null) ? "vulkan" : "cuda";
            res.Add(new TargetAssignment { CanonicalIndex = idx, Backend = b });
        }
        return res;
    }

    static void PrintHelp() {
        Console.WriteLine("NV-KeepAlive v" + Version + " (Windows - Vulkan Compute + CUDA Driver API)");
        Console.WriteLine("Prevents idle voltage-drop TDRs (nvlddmkm 14/153) and BSOD 0x10E on NVIDIA GPUs.");
        Console.WriteLine("Usage: NvKeepAlive.exe [options]");
        Console.WriteLine("  --targets <spec>       Per-GPU backend list, e.g. '0:vulkan' or '0:cuda,1:vulkan'");
        Console.WriteLine("  --backend <mode>       'vulkan' (default) or 'cuda' (used when --targets is omitted)");
        Console.WriteLine("  --device <spec>        'auto' (mining cards, else GPU 0), 'all', or '0,1'");
        Console.WriteLine("  --list                 List detected NVIDIA GPUs (PCI Bus ID order)");
        Console.WriteLine("  --list-tsv             Machine-readable GPU list for GUI");
        Console.WriteLine("  --check-vulkan         Check Vulkan loader & NVIDIA ICD (OK / NO_LOADER / NO_NVIDIA_ICD)");
        Console.WriteLine("  --status               Show service status, Vulkan availability, GPUs, and recent log");
        Console.WriteLine("  --retrain-gen2         Fix PCIe Gen1->Gen2 on chipset/secondary slots for CMP 40HX");
        Console.WriteLine("  --dry-run              With --retrain-gen2: inspect PCIe links without writing");
        Console.WriteLine("  --wait <sec>           With --retrain-gen2: wait up to N sec for Gen2 capability (default: 25)");
        Console.WriteLine("  --driver <path>        With --retrain-gen2: WinRing0x64.sys path");
        Console.WriteLine("  --blocks <N>           Compute workgroups / grid blocks (default: 2)");
        Console.WriteLine("  --threads <N>          Threads per workgroup (default: 64)");
        Console.WriteLine("  --iters <N>            FMA iterations per launch (default: 450000 Vulkan / 120000 CUDA)");
        Console.WriteLine("  --sleep <ms>           Idle sleep ms between launches (default: 0)");
        Console.WriteLine("  --yield-ms <ms>        Adaptive sleep ms when GPU is busy (default: 150)");
        Console.WriteLine("  --no-adaptive          Disable adaptive yield during heavy GPU load");
        Console.WriteLine("  --pause-file <path>    Manual pause flag file (default: <exe_dir>\\keepalive.pause)");
        Console.WriteLine("  --log <path>           Event-only log file (100 KB rotation)");
        Console.WriteLine("  --sec <N>              Run for N seconds and exit (0 = forever, default: 0)");
        Console.WriteLine("  --wait-timeout <N>     Seconds to wait for NVIDIA GPU at boot before exiting cleanly (default: 30)");
        Console.WriteLine("  --no-mutex             Allow multiple instances (for testing)");
        Console.WriteLine("  --quiet                Minimal console output");
    }

    static int Main(string[] args) {
        var cfg = new Config();
        cfg.PauseFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keepalive.pause");

        for (int i = 0; i < args.Length; i++) {
            string a = args[i];
            if (a == "-h" || a == "--help") { PrintHelp(); return 0; }
            else if (a == "--list") cfg.ListOnly = true;
            else if (a == "--list-tsv") cfg.ListTsv = true;
            else if (a == "--status") cfg.StatusOnly = true;
            else if (a == "--check-vulkan") cfg.CheckVulkan = true;
            else if (a == "--retrain-gen2") cfg.RetrainGen2 = true;
            else if (a == "--dry-run" || a == "-n") cfg.DryRun = true;
            else if (a == "--wait" && i + 1 < args.Length) cfg.RetrainWaitSec = int.Parse(args[++i]);
            else if (a == "--driver" && i + 1 < args.Length) cfg.DriverPath = args[++i];
            else if (a == "--targets" && i + 1 < args.Length) cfg.TargetsSpec = args[++i];
            else if (a == "--backend" && i + 1 < args.Length) cfg.Backend = args[++i].ToLowerInvariant();
            else if (a == "--device" && i + 1 < args.Length) cfg.DevSpec = args[++i];
            else if (a == "--blocks" && i + 1 < args.Length) cfg.Blocks = uint.Parse(args[++i]);
            else if (a == "--threads" && i + 1 < args.Length) cfg.Threads = uint.Parse(args[++i]);
            else if (a == "--iters" && i + 1 < args.Length) cfg.Iters = uint.Parse(args[++i]);
            else if (a == "--sleep" && i + 1 < args.Length) cfg.SleepMs = int.Parse(args[++i]);
            else if (a == "--yield-ms" && i + 1 < args.Length) cfg.YieldMs = int.Parse(args[++i]);
            else if (a == "--no-adaptive") cfg.Adaptive = false;
            else if (a == "--pause-file" && i + 1 < args.Length) cfg.PauseFile = args[++i];
            else if (a == "--log" && i + 1 < args.Length) cfg.LogFile = args[++i];
            else if (a == "--sec" && i + 1 < args.Length) cfg.RunSec = int.Parse(args[++i]);
            else if (a == "--wait-timeout" && i + 1 < args.Length) cfg.WaitTimeoutSec = int.Parse(args[++i]);
            else if (a == "--no-mutex") cfg.UseMutex = false;
            else if (a == "--quiet") cfg.Quiet = true;
        }

        if (cfg.CheckVulkan) return RunCheckVulkan();
        if (cfg.ListTsv) return PrintDevicesTsv();
        if (cfg.ListOnly) return PrintDevices();
        if (cfg.StatusOnly) return PrintStatus(cfg);
        if (cfg.RetrainGen2) return RunRetrainGen2(cfg);

        g_activeCfg = cfg;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; g_shutdown = true; g_stop = true; g_needRestart = false; };
        try { SetProcessShutdownParameters(0x100, 0); } catch {}
        g_ctrlHandler = (uint ctrlType) => {
            if (ctrlType == 5) return true; // CTRL_LOGOFF_EVENT: stay alive as SYSTEM service
            if (!g_shutdown) {
                g_shutdown = true;
                g_stop = true;
                g_needRestart = false;
                if (g_activeCfg != null) {
                    LogEvent(g_activeCfg, true, "Shutdown/stop signal (ctrl={0}) received - releasing GPU context cleanly.", ctrlType);
                }
            }
            try { g_doneEvent.Wait(2500); } catch {}
            return true;
        };
        try { SetConsoleCtrlHandler(g_ctrlHandler, true); } catch {}

        Mutex mutex = null;
        if (cfg.UseMutex) {
            bool createdNew = false;
            try { mutex = new Mutex(true, MutexName, out createdNew); }
            catch (UnauthorizedAccessException) { createdNew = false; }
            if (!createdNew) {
                if (!cfg.Quiet) {
                    Console.WriteLine("NV-KeepAlive is already running in the background.");
                    Console.WriteLine("Launch NvKeepAliveUI.exe to manage the service.");
                    Console.WriteLine();
                    PrintStatus(cfg);
                }
                return 0;
            }
        }

        ClearStalePauseOnStartup(cfg);
        InitNvmlOnce();
        IntPtr vkInst = IntPtr.Zero;
        try {
            var totalSw = Stopwatch.StartNew();
            List<CanonicalGpu> gpus = null;
            bool loggedWait = false, timedOut = false;
            var waitSw = Stopwatch.StartNew();

            while (!g_stop && (cfg.RunSec == 0 || totalSw.Elapsed.TotalSeconds < cfg.RunSec)) {
                if (vkInst != IntPtr.Zero) { try { vkDestroyInstance(vkInst, IntPtr.Zero); } catch {} vkInst = IntPtr.Zero; }
                gpus = ProbeAllGpus(out vkInst);
                if (gpus.Count > 0) break;
                if (cfg.WaitTimeoutSec > 0 && waitSw.Elapsed.TotalSeconds >= cfg.WaitTimeoutSec) {
                    timedOut = true;
                    break;
                }
                if (!loggedWait) {
                    LogEvent(cfg, true, "Waiting for NVIDIA GPU/driver (timeout={0}s)...", cfg.WaitTimeoutSec);
                    loggedWait = true;
                }
                Thread.Sleep(2000);
            }
            if (g_stop) return 0;
            if (timedOut || gpus == null || gpus.Count == 0) {
                LogEvent(cfg, true, "No NVIDIA GPU detected after {0}s - exiting cleanly.", cfg.WaitTimeoutSec);
                return 0;
            }

            var assignments = ResolveAssignments(cfg, gpus);
            var valid = new List<TargetAssignment>();
            foreach (var ta in assignments) {
                var g = gpus[ta.CanonicalIndex];
                if (ta.Backend == "vulkan" && g.Vk == null) {
                    LogEvent(cfg, true, "[GPU {0}] Requested Vulkan backend, but Vulkan is unavailable on {1}.", g.Index, g.Name);
                    continue;
                }
                if (ta.Backend == "cuda" && g.Cuda == null) {
                    LogEvent(cfg, true, "[GPU {0}] Requested CUDA backend, but CUDA is unavailable on {1}.", g.Index, g.Name);
                    continue;
                }
                valid.Add(ta);
            }
            if (valid.Count == 0) {
                LogEvent(cfg, true, "No valid GPU/backend targets could be initialized.");
                return 1;
            }

            // Release discovery VkInstance before workers start (each Vulkan worker manages its own fresh instance)
            if (vkInst != IntPtr.Zero) { try { vkDestroyInstance(vkInst, IntPtr.Zero); } catch {} vkInst = IntPtr.Zero; }

            var summaryParts = new List<string>();
            foreach (var ta in valid) summaryParts.Add(ta.CanonicalIndex + ":" + ta.Backend);
            LogEvent(cfg, true, "NV-KeepAlive v{0} started: targets=[{1}] blocks={2} threads={3} adaptive={4} yield={5}ms",
                Version, string.Join(",", summaryParts.ToArray()), cfg.Blocks, cfg.Threads, cfg.Adaptive, cfg.YieldMs);

            uint vkIters = cfg.Iters > 0 ? cfg.Iters : 450000u;
            uint cuIters = cfg.Iters > 0 ? cfg.Iters : 120000u;
            byte[] ptxBytes = Encoding.ASCII.GetBytes(PtxSource + "\0");

            if (valid.Count == 1) {
                var ta = valid[0];
                if (ta.Backend == "vulkan") RunVulkanWorker(gpus[ta.CanonicalIndex], cfg, vkIters, totalSw);
                else RunCudaWorker(gpus[ta.CanonicalIndex], cfg, cuIters, ptxBytes, totalSw);
            } else {
                var threads = new List<Thread>();
                foreach (var ta in valid) {
                    var capTa = ta;
                    var capGpu = gpus[capTa.CanonicalIndex];
                    var t = new Thread(() => {
                        if (capTa.Backend == "vulkan") RunVulkanWorker(capGpu, cfg, vkIters, totalSw);
                        else RunCudaWorker(capGpu, cfg, cuIters, ptxBytes, totalSw);
                    });
                    t.IsBackground = false;
                    t.Start();
                    threads.Add(t);
                }
                foreach (var t in threads) t.Join();
            }
        } finally {
            if (vkInst != IntPtr.Zero) { try { vkDestroyInstance(vkInst, IntPtr.Zero); } catch {} }
            if (g_nvmlShutdown != null) { try { g_nvmlShutdown(); } catch {} }
            if (mutex != null) {
                try { mutex.ReleaseMutex(); } catch {}
                mutex.Dispose();
            }
            g_doneEvent.Set();
        }

        if (g_needRestart && !g_shutdown) {
            Thread.Sleep(1500);
            try {
                var sb = new StringBuilder();
                foreach (string a in args) {
                    if (sb.Length > 0) sb.Append(' ');
                    if (a.IndexOf(' ') >= 0 || a.IndexOf('\t') >= 0 || a.Length == 0)
                        sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                    else
                        sb.Append(a);
                }
                Process.Start(new ProcessStartInfo {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = sb.ToString(),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            } catch (Exception ex) {
                LogEvent(cfg, true, "Auto-restart failed: {0}", ex.Message);
            }
        }
        return 0;
    }
}
