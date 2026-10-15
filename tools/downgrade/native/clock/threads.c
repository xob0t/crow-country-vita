/* CrowClock's thread functions, for compat/Threads.cs: which of the game's threads use the CPU, from
   the kernel's own accounting (each thread's run time, each CPU's idle time), and a thread's priority
   and CPU affinity changed for A/B runs. The PS Vita has no sampling profiler; the FMOD mixer, the
   loading thread and the render worker are threads the scripts never see. */
#include <kernel.h>
#include <string.h>

#define PRX_EXPORT __declspec(dllexport)

/* The calling thread's UID. */
PRX_EXPORT int CrowThreadSelf(void)
{
	return sceKernelGetThreadId();
}

/* A thread's state into out[10]: initial and current priority, current CPU affinity mask, current
   CPU, run time in microseconds (low, high word), status, preemptions by other threads and by
   interrupts, times it gave up the CPU itself; its name into name[32]. 0 or the system's error. */
PRX_EXPORT int CrowThreadQuery(int uid, int *out, char *name)
{
	SceKernelThreadInfo info;
	int result;
	memset(&info, 0, sizeof info);
	info.size = sizeof info;
	result = sceKernelGetThreadInfo(uid, &info);
	if (result < 0) return result;
	out[0] = info.initPriority;
	out[1] = info.currentPriority;
	out[2] = info.currentCpuAffinityMask;
	out[3] = info.currentCpuId;
	out[4] = (int)info.runClocks.u.low;
	out[5] = (int)info.runClocks.u.hi;
	out[6] = (int)info.status;
	out[7] = (int)info.threadPreemptCount;
	out[8] = (int)info.intrPreemptCount;
	out[9] = (int)info.threadReleaseCount;
	memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	return 0;
}

/* Sets a thread's priority (when above 0) and CPU affinity mask (when above 0). 0 or the first error. */
PRX_EXPORT int CrowThreadSet(int uid, int priority, int affinity)
{
	int result;
	if (priority > 0)
	{
		result = sceKernelChangeThreadPriority(uid, priority);
		if (result < 0) return result;
	}
	if (affinity > 0)
	{
		result = sceKernelChangeThreadCpuAffinityMask(uid, affinity);
		if (result < 0) return result;
	}
	return 0;
}

/* The UID of the thread each of the first three CPUs runs now, into out[3]; 0 for another process's
   thread or none. 0 or the system's error. */
PRX_EXPORT int CrowThreadsRunning(int *out)
{
	SceKernelThreadRunStatus status;
	SceUID process = sceKernelGetProcessId();
	int result, cpu;
	memset(&status, 0, sizeof status);
	status.size = sizeof status;
	result = sceKernelGetThreadRunStatus(&status);
	if (result < 0) return result;
	for (cpu = 0; cpu < 3; cpu++)
		out[cpu] = status.cpuInfo[cpu].processId == process ? status.cpuInfo[cpu].threadId : 0;
	return 0;
}

/* Each of the first three CPUs' idle time in microseconds into out[6] (low, high word per CPU). 0 or
   the system's error. */
PRX_EXPORT int CrowCpuIdle(int *out)
{
	SceKernelSystemInfo info;
	int result, cpu;
	memset(&info, 0, sizeof info);
	info.size = sizeof info;
	result = sceKernelGetSystemInfo(&info);
	if (result < 0) return result;
	for (cpu = 0; cpu < 3; cpu++)
	{
		out[cpu * 2] = (int)info.cpuInfo[cpu].idleClock.u.low;
		out[cpu * 2 + 1] = (int)info.cpuInfo[cpu].idleClock.u.hi;
	}
	return 0;
}
