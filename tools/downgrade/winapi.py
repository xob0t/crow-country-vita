"""The Windows calls lock.py, vitalike.py and cpuprofile.py share: one kernel32 with their argument
types, job objects, and the constants they pass."""

import ctypes
import ctypes.wintypes as wt

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

PROCESS_ALL_ACCESS = 0x1F0FFF
HIGH_PRIORITY_CLASS = 0x80  # SetPriorityClass, and a creation flag
CREATE_SUSPENDED = 0x4
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9  # JOBOBJECTINFOCLASS


class JobLimits(ctypes.Structure):  # JOBOBJECT_EXTENDED_LIMIT_INFORMATION, its BASIC_LIMIT_INFORMATION inline
    _fields_ = [
        ('PerProcessUserTimeLimit', ctypes.c_int64),
        ('PerJobUserTimeLimit', ctypes.c_int64),
        ('LimitFlags', wt.DWORD),
        ('MinimumWorkingSetSize', ctypes.c_size_t),
        ('MaximumWorkingSetSize', ctypes.c_size_t),
        ('ActiveProcessLimit', wt.DWORD),
        ('Affinity', ctypes.c_size_t),
        ('PriorityClass', wt.DWORD),
        ('SchedulingClass', wt.DWORD),
        ('IoCounters', ctypes.c_uint64 * 6),
        ('ProcessMemoryLimit', ctypes.c_size_t),
        ('JobMemoryLimit', ctypes.c_size_t),
        ('PeakProcessMemoryUsed', ctypes.c_size_t),
        ('PeakJobMemoryUsed', ctypes.c_size_t),
    ]


kernel32.GetCurrentProcess.restype = wt.HANDLE
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
kernel32.SetProcessAffinityMask.argtypes = [wt.HANDLE, ctypes.c_size_t]
kernel32.SetPriorityClass.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.CreateJobObjectW.restype = wt.HANDLE
kernel32.SetInformationJobObject.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD]
kernel32.AssignProcessToJobObject.argtypes = [wt.HANDLE, wt.HANDLE]
kernel32.TerminateJobObject.argtypes = [wt.HANDLE, wt.UINT]


def check(ok, what):
    """ok, or an OSError with Windows' last error when it is 0 or None."""
    if not ok:
        raise OSError(ctypes.get_last_error(), f'{what} failed')
    return ok


def kill_on_close_job():
    """A new job object that ends its processes when its last handle closes."""
    job = check(kernel32.CreateJobObjectW(None, None), 'CreateJobObject')
    limits = JobLimits(LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
    size = ctypes.sizeof(limits)
    check(
        kernel32.SetInformationJobObject(job, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, ctypes.byref(limits), size),
        'SetInformationJobObject',
    )
    return job
