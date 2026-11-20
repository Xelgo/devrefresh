#nullable disable
#pragma warning disable SYSLIB1099
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DevRefresh;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class OneCCom
{
    // Load the selected connector and its type information without changing COM registration.
    private static readonly List<Dispatch> objects = new();
    public static void ReleaseAll()
    {
        for(var i=objects.Count-1;i>=0;i--)try{objects[i].Dispose();}catch(COMException){}catch(InvalidComObjectException){}
        objects.Clear();
    }
    [ComImport,Guid("00020401-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRawTypeInfo
    {
        void Skip0();void Skip1();void Skip2();void Skip3();void Skip4();void Skip5();void Skip6();void Skip7();
        [PreserveSig] int Invoke(IntPtr instance,int id,short flags,ref System.Runtime.InteropServices.ComTypes.DISPPARAMS parameters,[MarshalAs(UnmanagedType.Struct)]out object result,out System.Runtime.InteropServices.ComTypes.EXCEPINFO error,out uint argument);
    }
    [DllImport("oleaut32",CharSet=CharSet.Unicode,PreserveSig=false)]
    static extern void LoadTypeLibEx(string path,int registration,out System.Runtime.InteropServices.ComTypes.ITypeLib library);
    [ComImport,Guid("00020400-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatch
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint number,uint locale,out IntPtr value);
        [PreserveSig] int GetIDsOfNames(ref Guid iid,[MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.LPWStr,SizeParamIndex=2)]string[] names,uint count,uint locale,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)]int[] ids);
        [PreserveSig] int Invoke(int id,ref Guid iid,uint locale,ushort flags,ref System.Runtime.InteropServices.ComTypes.DISPPARAMS parameters,[MarshalAs(UnmanagedType.Struct)]out object result,out System.Runtime.InteropServices.ComTypes.EXCEPINFO error,out uint argument);
    }
    [DllImport("oleaut32")] static extern int VariantClear(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FillException(ref System.Runtime.InteropServices.ComTypes.EXCEPINFO error);
    public sealed class Dispatch : IDisposable
    {
        object value;
        System.Runtime.InteropServices.ComTypes.ITypeInfo info;
        internal Dispatch(object value){this.value=value;objects.Add(this);}
        internal Dispatch(object value,System.Runtime.InteropServices.ComTypes.ITypeInfo info):this(value){this.info=info;}
        public object Call(string name,params object[] args){return Invoke(name,1,args);}
        public object Get(string name){return Invoke(name,2,new object[0]);}
        public void Set(string name,object value){Invoke(name,4,new[]{value});}
        object Invoke(string name,ushort flags,object[] args)
        {
            var dispatch=(IDispatch)value;var nil=Guid.Empty;var ids=new int[1];
            if(info!=null)info.GetIDsOfNames(new[]{name},1,ids);
            else{var idhr=dispatch.GetIDsOfNames(ref nil,new[]{name},1,0,ids);if(idhr<0)throw new COMException("GetIDsOfNames "+name,idhr);}
            var size=IntPtr.Size==8?24:16;var variants=Marshal.AllocCoTaskMem(size*args.Length);var named=IntPtr.Zero;
            int populated=0;
            try
            {
                for(int i=0;i<args.Length;i++){var arg=args[args.Length-i-1];Marshal.GetNativeVariantForObject(arg is Dispatch?((Dispatch)arg).value:arg,IntPtr.Add(variants,size*i));populated++;}
                var parameters=new System.Runtime.InteropServices.ComTypes.DISPPARAMS{rgvarg=variants,cArgs=args.Length};
                if(flags==4){named=Marshal.AllocCoTaskMem(4);Marshal.WriteInt32(named,-3);parameters.rgdispidNamedArgs=named;parameters.cNamedArgs=1;}
                object result;System.Runtime.InteropServices.ComTypes.EXCEPINFO error;uint index;
                int hr=0;
                if(info!=null)
                {
                    IntPtr attrp;info.GetTypeAttr(out attrp);Guid guid;
                    try{guid=((System.Runtime.InteropServices.ComTypes.TYPEATTR)Marshal.PtrToStructure(attrp,typeof(System.Runtime.InteropServices.ComTypes.TYPEATTR))).guid;}finally{info.ReleaseTypeAttr(attrp);}
                    var unk=Marshal.GetIUnknownForObject(value);IntPtr target=IntPtr.Zero;
                    try{Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk,in guid,out target));hr=((IRawTypeInfo)info).Invoke(target,ids[0],(short)flags,ref parameters,out result,out error,out index);}
                    finally{if(target!=IntPtr.Zero)Marshal.Release(target);Marshal.Release(unk);}
                }
                else hr=dispatch.Invoke(ids[0],ref nil,0,flags,ref parameters,out result,out error,out index);
                if(hr<0){if(error.pfnDeferredFillIn!=IntPtr.Zero)((FillException)Marshal.GetDelegateForFunctionPointer(error.pfnDeferredFillIn,typeof(FillException)))(ref error);throw new COMException("Invoke "+name+" ("+hr.ToString("X8")+"): "+error.bstrDescription,hr);}
                return Wrap(result);
            }
            finally{for(int i=0;i<populated;i++)VariantClear(IntPtr.Add(variants,i*size));Marshal.FreeCoTaskMem(variants);if(named!=IntPtr.Zero)Marshal.FreeCoTaskMem(named);}
        }
        static object Wrap(object result)
        {
            if(result==null)return null;
            if(Marshal.IsComObject(result))return new Dispatch(result);
            var array=result as Array;
            if(array!=null){var wrapped=new object[array.Length];for(int i=0;i<array.Length;i++)wrapped[i]=Wrap(array.GetValue(i));return wrapped;}
            return result;
        }
        public void Dispose(){if(value!=null){Marshal.ReleaseComObject(value);value=null;}}
    }
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
    [DllImport("kernel32",CharSet=CharSet.Ansi,SetLastError=true)]
    static extern IntPtr GetProcAddress(IntPtr module,string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetFactory(ref Guid clsid,ref Guid iid,out IntPtr factory);
    [ComImport,Guid("00000001-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer,ref Guid iid,out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool value);
    }
    public static object Create(string bin)
    {
        var module=LoadLibraryEx(Path.Combine(bin,"comcntr.dll"),IntPtr.Zero,8);
        if(module==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var export=GetProcAddress(module,"DllGetClassObject");
        if(export==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var getter=(GetFactory)Marshal.GetDelegateForFunctionPointer(export,typeof(GetFactory));
        var clsid=new Guid("181E893D-73A4-4722-B61D-D604B3D67D47");
        var iid=new Guid("00000001-0000-0000-C000-000000000046");
        IntPtr factoryPtr;
        Marshal.ThrowExceptionForHR(getter(ref clsid,ref iid,out factoryPtr));
        var factory=(IClassFactory)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);
        try
        {
            var dispatch=new Guid("00020400-0000-0000-C000-000000000046");
            IntPtr value;Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero,ref dispatch,out value));
            try{
                System.Runtime.InteropServices.ComTypes.ITypeLib lib;LoadTypeLibEx(Path.Combine(bin,"comcntr.dll"),2,out lib);
                System.Runtime.InteropServices.ComTypes.ITypeInfo coclass;lib.GetTypeInfoOfGuid(ref clsid,out coclass);
                int href;coclass.GetRefTypeOfImplType(0,out href);System.Runtime.InteropServices.ComTypes.ITypeInfo info;coclass.GetRefTypeInfo(href,out info);
                Marshal.FinalReleaseComObject(coclass);Marshal.FinalReleaseComObject(lib);
                return new Dispatch(Marshal.GetObjectForIUnknown(value),info);
            }finally{Marshal.Release(value);}
        }
        finally{Marshal.FinalReleaseComObject(factory);}
    }
}
