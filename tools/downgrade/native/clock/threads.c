/* CrowClock's thread functions, for compat/Threads.cs and compat/ProbeFiles.cs: which of the game's
   threads use the CPU, from the kernel's own accounting (each thread's run time, each CPU's idle time), and a thread's priority
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

/* For Quality's frame probe: a thread's status, wait type, the UID of what it waits on and the CPU it
   last ran on, into out[4]. 0 or the system's error. */
PRX_EXPORT int CrowThreadWait(int uid, int *out)
{
	SceKernelThreadInfo info;
	int result;
	memset(&info, 0, sizeof info);
	info.size = sizeof info;
	result = sceKernelGetThreadInfo(uid, &info);
	if (result < 0) return result;
	out[0] = (int)info.status;
	out[1] = (int)info.waitType;
	out[2] = (int)info.waitId;
	out[3] = info.lastExecutedCpuId;
	return 0;
}

/* The name of a synchronization object a thread waits on (CrowThreadWait's wait type and UID) into
   name[32]: event flags, semaphores, mutexes, condition variables, events, lightweight mutexes and
   condition variables. 0, or below 0 when the type has no name lookup here or the lookup failed. */
PRX_EXPORT int CrowSyncName(int uid, int waitType, char *name)
{
	unsigned int type = (unsigned int)waitType & 0x7fffffffU;
	int result = -1;
	name[0] = 0;
	if (type & SCE_KERNEL_WAITTYPE_EVENTFLAG)
	{
		SceKernelEventFlagInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetEventFlagInfo(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & SCE_KERNEL_WAITTYPE_SEMAPHORE)
	{
		SceKernelSemaInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetSemaInfo(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & SCE_KERNEL_WAITTYPE_MUTEX)
	{
		SceKernelMutexInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetMutexInfo(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & (SCE_KERNEL_WAITTYPE_COND_SIGNAL | SCE_KERNEL_WAITTYPE_COND_MUTEX))
	{
		SceKernelCondInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetCondInfo(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & (SCE_KERNEL_WAITTYPE_EVENT | SCE_KERNEL_WAITTYPE_MP_EVENTS))
	{
		SceKernelEventInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetEventInfo(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & SCE_KERNEL_WAITTYPE_LW_MUTEX)
	{
		SceKernelLwMutexInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetLwMutexInfoById(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	else if (type & (SCE_KERNEL_WAITTYPE_LW_COND_SIGNAL | SCE_KERNEL_WAITTYPE_LW_COND_LW_MUTEX))
	{
		SceKernelLwCondInfo info;
		memset(&info, 0, sizeof info);
		info.size = sizeof info;
		result = sceKernelGetLwCondInfoById(uid, &info);
		if (result >= 0) memcpy(name, info.name, SCE_UID_NAMELEN + 1);
	}
	name[SCE_UID_NAMELEN] = 0;
	return result;
}
