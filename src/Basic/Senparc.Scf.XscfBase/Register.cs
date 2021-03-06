﻿using Microsoft.Extensions.DependencyInjection;
using Senparc.CO2NET.Cache;
using Senparc.CO2NET.Helpers;
using Senparc.CO2NET.Trace;
using Senparc.Scf.Core.Enums;
using Senparc.Scf.Core.Exceptions;
using Senparc.Scf.Core.Models.DataBaseModel;
using Senparc.Scf.Service;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Senparc.Scf.XscfBase
{
    public static class Register
    {
        /// <summary>
        /// 模块和方法集合 TODO：可放置到缓存中
        /// </summary>
        public static List<IXscfRegister> RegisterList { get; set; } = new List<IXscfRegister>();

        /// <summary>
        /// 初始化扫描
        /// </summary>
        /// <returns></returns>
        public static string StartEngine(this IServiceCollection services)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{SystemTime.Now}] 开始初始化扫描 XscfModules");
            var scanTypesCount = 0;
            IEnumerable<Type> types = null;
            //var cache = CacheStrategyFactory.GetObjectCacheStrategyInstance();
            //using (cache.BeginCacheLock("Senparc.Scf.XscfBase.Register", "Scan")) //在注册阶段还未完成缓存配置
            {

                try
                {
                    types = AppDomain.CurrentDomain.GetAssemblies()
                               .SelectMany(a =>
                               {
                                   try
                                   {
                                       scanTypesCount++;
                                       var aTypes = a.GetTypes();
                                       return aTypes.Where(t =>
                                            !t.IsAbstract &&
                                            (t.GetInterfaces().Contains(typeof(IXscfRegister)) ||
                                            t.GetInterfaces().Contains(typeof(IXscfFunction)/* 暂时不收录 */)
                                            ));
                                   }
                                   catch (Exception ex)
                                   {
                                       sb.AppendLine($"[{SystemTime.Now}] 自动扫描程序集异常：" + a.FullName);
                                       SenparcTrace.SendCustomLog("XscfRegister() 自动扫描程序集异常：" + a.FullName, ex.ToString());
                                       return new List<Type>();//不能 return null
                                   }
                               });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"扫描程集异常退出，可能无法获得完整程序集信息：{ex.Message}");
                }

                if (types != null)
                {
                    sb.AppendLine($"[{SystemTime.Now}] 满足条件对象：{types.Count()}");

                    //先注册
                    foreach (var type in types.Where(z => z != null && z.GetInterfaces().Contains(typeof(IXscfRegister))))
                    {
                        sb.AppendLine($"[{SystemTime.Now}] 扫描到 IXscfRegister：{type.FullName}");

                        var register = type.Assembly.CreateInstance(type.FullName) as IXscfRegister;

                        if (!RegisterList.Contains(register))
                        {
                            RegisterList.Add(register);
                            services.AddScoped(type);//DI 中注册
                            foreach (var functionType in register.Functions)
                            {
                                services.AddScoped(functionType);//DI 中注册
                            }
                        }
                    }

                    /* 暂时不收录 */
                    ////再扫描具体方法
                    //foreach (var type in types.Where(z => z != null && z.GetInterfaces().Contains(typeof(IXscfFunction))))
                    //{
                    //    sb.AppendLine($"[{SystemTime.Now}] 扫描到 IXscfFunction：{type.FullName}");

                    //    if (!ModuleFunctionCollection.ContainsKey(type))
                    //    {
                    //        throw new SCFExceptionBase($"{type.FullName} 未能提供正确的注册方法！");
                    //    }

                    //    var function = type as IXscfFunction;
                    //    ModuleFunctionCollection[type].Add(function);
                    //}
                }
            }
            sb.AppendLine($"[{SystemTime.Now}] 初始化扫描结束，共扫描 {scanTypesCount} 个程序集");
            return sb.ToString();
        }

        /// <summary>
        /// 扫描并安装
        /// </summary>
        /// <param name="xscfModules">现有已安装的模块</param>
        /// <param name="xscfModuleService">XscfModuleService</param>
        /// <param name="afterInstalledOrUpdated">安装或更新后执行</param>
        /// <param name="justScanThisUid">只扫描并更新特定的Uid</param>
        /// <returns></returns>
        public static async Task<string> ScanAndInstall(IList<CreateOrUpdate_XscfModuleDto> xscfModules,
            XscfModuleService xscfModuleService,
            Action<IXscfRegister, InstallOrUpdate> afterInstalledOrUpdated = null,
            string justScanThisUid = null)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{SystemTime.Now}] 开始扫描 XscfModules");

            //先注册
            var updatedCount = 0;
            var cache = CacheStrategyFactory.GetObjectCacheStrategyInstance();
            using (await cache.BeginCacheLockAsync("Senparc.Scf.XscfBase.Register", "Scan").ConfigureAwait(false))
            {
                foreach (var register in RegisterList)
                {
                    sb.AppendLine($"[{SystemTime.Now}] 扫描到 IXscfRegister：{register.GetType().FullName}");
                    if (justScanThisUid != null && register.Uid != justScanThisUid)
                    {
                        sb.AppendLine($"[{SystemTime.Now}] 由于只要求更新 uid：[{justScanThisUid}]，此模块跳过");
                        continue;
                    }
                    else
                    {
                        sb.AppendLine($"[{SystemTime.Now}] 符合尝试安装/更新要求，继续执行");
                    }

                    var xscfModuleStoredDto = xscfModules.FirstOrDefault(z => z.Uid == register.Uid);
                    var xscfModuleAssemblyDto = new UpdateVersion_XscfModuleDto(register.Name, register.Uid, register.MenuName, register.Version, register.Description);

                    //检查更新，并安装到数据库

                    var installOrUpdate = await xscfModuleService.CheckAndUpdateVersionAsync(xscfModuleStoredDto, xscfModuleAssemblyDto).ConfigureAwait(false);
                    sb.AppendLine($"[{SystemTime.Now}] 是否更新版本：{installOrUpdate?.ToString() ?? "未安装"}");

                    if (installOrUpdate.HasValue)
                    {
                        updatedCount++;

                        //执行安装程序
                        await register.InstallOrUpdateAsync(installOrUpdate.Value).ConfigureAwait(false);

                        afterInstalledOrUpdated?.Invoke(register, installOrUpdate.Value);
                    }
                }
            }

            sb.AppendLine($"[{SystemTime.Now}] 扫描结束，共新增或更新 {updatedCount} 个程序集");
            return sb.ToString();
        }
    }
}
