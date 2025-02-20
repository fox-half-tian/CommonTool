﻿using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SqlInfoGen.Cons.Bean.Config;
using SqlInfoGen.Cons.Common;
using SqlInfoGen.Cons.Enums;
using SqlInfoGen.Cons.Utils;
using SqlInfoGen.Cons.Utils.ViewGen;

namespace SqlInfoGen.Cons.Helpers;

public static class DbFileGenHelper
{
    public static async Task GenDbTableFileBatchAsync(List<DbConfigBean> dbBeans)
    {
        var tasks = new List<Task>();
        foreach (var dbBean in dbBeans)
        {
            tasks.Add(GenDbTableFileWithResourceControlAsync(dbBean));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task GenDbTableFileWithResourceControlAsync(DbConfigBean dbBean)
    {
        try
        {
            var stopwatch = new Stopwatch();

            // async ()=> {} 是定义的异步操作，并不会立即执行
            await ResourceSharer.FileHandleWithResourceControlAsync(async () =>
            {
                await GenDbTableFileAsync(dbBean);
            });
            
            stopwatch.Stop();
            
            Console.WriteLine($"{dbBean.ReadFilePath} 配置文件解析成功，生成的输出文件路径 {dbBean.OutputFilePath}，生成（+资源等待）耗时为 {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{dbBean.ReadFilePath} 中存在错误信息: {e.Message}");
        }
    }

    private static async Task GenDbTableFileAsync(DbConfigBean dbBean)
    {
        // 配置输出文件信息
        SetDbTableFileInfo(dbBean);
        // 检查是否遵循规范
        var dbErrorInfo = CheckDbFollowRule(dbBean);
        if (!string.IsNullOrWhiteSpace(dbErrorInfo))
        {
            Console.WriteLine($"{dbBean.ReadFilePath} 中存在错误信息: {dbErrorInfo}");
            return;
        }

        var tableBeans = JsonToObjUtils.GetTableConfigBeanList<TableConfigBean>(dbBean.ReadFilePath);
        // 检查输出目录是否存在
        if (!Directory.Exists(dbBean.OutputDir))
        {
            Directory.CreateDirectory(dbBean.OutputDir);
        }

        // 输出流
        await using var writer = new StreamWriter(dbBean.OutputFilePath);
        foreach (var tableBean in tableBeans)
        {
            // JsonSerializer 引用路径：System.Text.Json
            // Console.WriteLine(JsonSerializer.Serialize(tableBean));

            // 获取表结构数据
            var tableFieldInfos = DbHelper.GetTableSchema(tableBean.Table, dbBean.ConnectionString);
            // 检查是否遵循规则
            var tableErrorInfo = CheckTableFollowRule(tableBean, tableFieldInfos);
            if (!string.IsNullOrWhiteSpace(tableErrorInfo))
            {
                Console.WriteLine($"{dbBean.ReadFilePath} 中存在错误信息: {tableErrorInfo}");
                continue;
            }

            // 如果需要所有字段，则修改 bean 的 Fields
            if (tableBean.NeedAllFields)
            {
                tableBean.Fields = tableFieldInfos.Values.OrderBy(f => f.Order).Select(f => new Field
                {
                    Name = f.Field,
                    // 将配置的别名赋给 Field
                    Alias = tableBean.Fields?.FirstOrDefault(r => r.Name == f.Field)?.Alias
                }).ToList();
            }

            // 生成 sql
            var sql = GenSql(tableBean);

            // 执行 sql
            var dataTable = DbHelper.GetDataTable(sql, dbBean.ConnectionString);

            // 生成该表的内容
            var tableCxt = GenFileContent(dbBean, tableBean, dataTable, tableFieldInfos);
            await writer.WriteAsync(tableCxt);
        }
    }

    private static string GenFileContent(DbConfigBean dbBean, TableConfigBean tableBean, DataTable dataTable,
        Dictionary<string, TableFieldInfo> tableFieldInfos)
    {
        switch (dbBean.OutputFileNameSuffixEnum)
        {
            case FileSuffixEnum.Md:
                return MarkdownGenUtils.GenMarkdown(tableBean, dataTable, tableFieldInfos);
            default:
                return string.Empty;
        }
    }

    private static void SetDbTableFileInfo(DbConfigBean dbBean)
    {
        if (dbBean.OutputDir == ConfigCommon.DefaultName || string.IsNullOrWhiteSpace(dbBean.OutputDir))
        {
            dbBean.OutputDir = ConfigCommon.DefaultOutputDir;
        }

        if (dbBean.OutputFileName == ConfigCommon.DefaultName || string.IsNullOrWhiteSpace(dbBean.OutputFileName))
        {
            dbBean.OutputFileName = $"{ConfigCommon.DefaultOutputFileNamePrefix}{dbBean.Db}";
        }

        if (dbBean.OutputFileNameSuffix == ConfigCommon.DefaultName ||
            string.IsNullOrWhiteSpace(dbBean.OutputFileNameSuffix))
        {
            dbBean.OutputFileNameSuffix = ConfigCommon.DefaultOutputFileNameSuffix;
        }

        dbBean.ReadFilePath = Path.Combine(Path.Combine(dbBean.ReadDirLevels.ToArray()), dbBean.ReadFileName);
        dbBean.OutputFilePath =
            $"{dbBean.OutputDir}{Path.DirectorySeparatorChar}{dbBean.OutputFileName}.{dbBean.OutputFileNameSuffix}";
    }

    private static string GenSql(TableConfigBean bean)
    {
        var builder = new StringBuilder();
        builder.Append("select ");
        // 配置查询的字段
        builder.Append(string.Join(",", bean.Fields!.Select(f => f.Name)));
        // 配置查询的表
        builder.Append($" from {bean.Table} ");
        // 配置查询条件
        if (bean.SelectConditions is not null && bean.SelectConditions.Count > 0)
        {
            builder.Append(
                $" where {string.Join(" and ", bean.SelectConditions.Select(condition => $" ({condition}) "))}");
        }

        // 配置排序规则
        if (bean.OrderByConditions is not null && bean.OrderByConditions.Count > 0)
        {
            builder.Append($" order by {string.Join(", ", bean.OrderByConditions)}");
        }

        // 配置返回数量
        builder.Append($" limit {bean.Limit.Offset}, {bean.Limit.Count}");

        return builder.ToString();
    }

    /// <summary>
    /// 检查输出文件配置是否正确
    /// </summary>
    /// <returns></returns>
    private static string? CheckDbFollowRule(DbConfigBean bean)
    {
        // 检查连接字符串是否进行配置
        if (string.IsNullOrWhiteSpace(bean.ConnectionString))
        {
            return "未正确配置参数 ConnectionString";
        }

        // 检查文件后缀是否提供支持
        var enumByDescription = EnumExtensions.ParseEnumByDescription<FileSuffixEnum>(bean.OutputFileNameSuffix);
        if (enumByDescription == null)
        {
            return $"OutputFileNameSuffix 为 [{bean.OutputFileNameSuffix}] 暂不支持生成";
        }

        bean.OutputFileNameSuffixEnum = enumByDescription.Value;

        return null;
    }

    /// <summary>
    /// 检查表配置是否正确
    /// </summary>
    /// <param name="bean"></param>
    /// <param name="tableFieldInfos"></param>
    /// <returns></returns>
    private static string? CheckTableFollowRule(TableConfigBean bean,
        Dictionary<string, TableFieldInfo> tableFieldInfos)
    {
        // 检查是否配置了 table
        if (string.IsNullOrWhiteSpace(bean.Table))
        {
            return "Table 不能为空";
        }

        // 如果没有配置了 NeedAllFields，则必须配置 Fields
        if (!bean.NeedAllFields && (bean.Fields == null || bean.Fields.Count == 0))
        {
            return "NeedAllFields 为 false 时，必须配置 Fields";
        }

        // 检查配置查询的字段是否都存在
        if (!bean.NeedAllFields)
        {
            var notExistsFields = new List<string>();
            foreach (var field in bean.Fields!)
            {
                if (!tableFieldInfos.ContainsKey(field.Name))
                {
                    notExistsFields.Add(field.Name);
                }
            }

            if (notExistsFields.Count > 0)
            {
                return $"{bean.Table} 中不存在的字段: {string.Join(",", notExistsFields)}。请仔细检查配置。";
            }
        }

        // 检查配置的 limit
        if (bean.Limit.Offset < 0 || bean.Limit.Count <= 0)
        {
            return "Limit 的 Offset 必须大于等于 0，Count 必须大于 0";
        }


        return null;
    }
}