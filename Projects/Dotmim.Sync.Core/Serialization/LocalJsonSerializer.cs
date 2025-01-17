﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.Serialization
{

    //public class LocalJsonSerializerFactory : ILocalSerializerFactory
    //{
    //    public string Key => "json";
    //    public ILocalSerializer GetLocalSerializer() => new LocalJsonSerializer();
    //}
    public class LocalJsonSerializer
    {
        private StreamWriter sw;
        private JsonTextWriter writer;

        public string Extension => "json";

        public async Task CloseFileAsync(string path, SyncTable shemaTable)
        {
            // Close file
            this.writer.WriteEndArray();
            this.writer.WriteEndObject();
            this.writer.WriteEndArray();
            this.writer.WriteEndObject();
            this.writer.Flush();
            await this.writer.CloseAsync();
            this.sw.Close();
        }
        public async Task OpenFileAsync(string path, SyncTable shemaTable)
        {
            if (this.writer != null)
            {
                await this.writer.CloseAsync();
                this.writer = null;
            }

            this.sw = new StreamWriter(path);
            this.writer = new JsonTextWriter(sw) { CloseOutput = true };

            this.writer.WriteStartObject();
            this.writer.WritePropertyName("t");
            this.writer.WriteStartArray();
            this.writer.WriteStartObject();
            this.writer.WritePropertyName("n");
            this.writer.WriteValue(shemaTable.TableName);
            this.writer.WritePropertyName("s");
            this.writer.WriteValue(shemaTable.SchemaName);
            //this.writer.WritePropertyName("c");
            //this.writer.WriteStartArray();
            //foreach (var c in shemaTable.Columns)
            //    this.writer.WriteValue(c.ColumnName);
            //this.writer.WriteEndArray();
            this.writer.WritePropertyName("r");
            this.writer.WriteStartArray();
            this.writer.WriteWhitespace(Environment.NewLine);

        }
        public async Task WriteRowToFileAsync(SyncRow row, SyncTable shemaTable)
        {
            writer.WriteStartArray();

            var innerRow = row.ToArray();

            if (this.writingRowAsync != null)
            {
                var str = await this.writingRowAsync(shemaTable, innerRow).ConfigureAwait(false);
                writer.WriteValue(str);
            }
            else
            {
                for (var i = 0; i < innerRow.Length; i++)
                    writer.WriteValue(innerRow[i]);

            }

            writer.WriteEndArray();
            writer.WriteWhitespace(Environment.NewLine);
            writer.Flush();
        }

        //private Func<SyncTable, object[], string> writingRow;
        //private Func<SyncTable, string, object[]> readingRow;
        private Func<SyncTable, object[], Task<string>> writingRowAsync;
        private Func<SyncTable, string, Task<object[]>> readingRowAsync;

        public void OnWritingRow(Func<SyncTable, object[], Task<string>> func) => this.writingRowAsync = func;

        public void OnReadingRow(Func<SyncTable, string, Task<object[]>> func) => this.readingRowAsync = func;

        public Task<long> GetCurrentFileSizeAsync()
            => this.sw != null && this.sw.BaseStream != null ?
                Task.FromResult(this.sw.BaseStream.Position / 1024L) :
                Task.FromResult(0L);

        public IEnumerable<SyncRow> ReadRowsFromFile(string path, SyncTable schemaTable)
        {
            if (!File.Exists(path))
                yield break;

            // this list is the list of columns included in the file.
            // we may have more columns than what we have on a side
            //
            var includedColumns = new List<string>();
            var isSameColumns = true;

            JsonSerializer serializer = new JsonSerializer();
            using var reader = new JsonTextReader(new StreamReader(path));
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "t")
                {
                    while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                    {
                        if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "n")
                        {
                            reader.Read();
                        }
                        else if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "s")
                        {
                            reader.Read();
                        }
                        else if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "c")
                        {
                            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                            {
                                if (reader.TokenType == JsonToken.StartArray)
                                {
                                    // get columns from array
                                    includedColumns = serializer.Deserialize<List<string>>(reader);

                                    if (includedColumns == null || includedColumns.Count == 0)
                                    {
                                        // if we dont have columns specified, we are assuming it's the same columns
                                        isSameColumns = true;
                                    }
                                    if (includedColumns != null && includedColumns.Count != schemaTable.Columns.Count)
                                    {
                                        // if we don't have the same columns count, we don't have same columns on both side
                                        isSameColumns = false;
                                    }
                                    else if (includedColumns != null && includedColumns.Count > 0)
                                    {
                                        // check if we have the same includedColumns as schematable
                                        for (int i = 0; i < includedColumns.Count; i++)
                                        {
                                            // column name from file
                                            var includedColumnName = includedColumns[i];
                                            // column from schematable
                                            var schemaTableColumnName = schemaTable.Columns[i].ColumnName;

                                            if (!string.Equals(includedColumnName, schemaTableColumnName, SyncGlobalization.DataSourceStringComparison))
                                            {
                                                isSameColumns = false;
                                                break;
                                            }

                                        }
                                        isSameColumns = true;
                                    }
                                    break;
                                }
                            }
                        }
                        else if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "r")
                        {
                            // Go to children of the array
                            reader.Read();
                            // read all array
                            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                            {
                                if (reader.TokenType == JsonToken.StartArray)
                                {
                                    object[] array = null;

                                    if (this.readingRowAsync != null)
                                    {
                                        var jArray = serializer.Deserialize<JArray>(reader);
                                        var value = jArray[0].Value<string>();
                                        array = this.readingRowAsync(schemaTable, value).GetAwaiter().GetResult();

                                    }
                                    else
                                    {
                                        array = serializer.Deserialize<object[]>(reader);
                                    }

                                    if (isSameColumns)
                                    {
                                        yield return new SyncRow(schemaTable, array);
                                    }
                                    else
                                    {
                                        // Buffer is +1 to store state
                                        var row = new object[schemaTable.Columns.Count + 1];
                                        row[0] = array[0];

                                        // we are iterating on the row and create a new one
                                        for (var index = 1; index < array.Length; index++)
                                        {
                                            // get the next columnName from row
                                            var includedColumnName = includedColumns[index - 1];

                                            var existSchemaTableColumn = schemaTable.Columns[includedColumnName];

                                            // if column exist, set the correct value in new row
                                            if (existSchemaTableColumn != null)
                                            {
                                                row[schemaTable.Columns.IndexOf(existSchemaTableColumn) + 1] = array[index];
                                            }

                                        }
                                        Array.Clear(array, 0, array.Length);
                                        yield return new SyncRow(schemaTable, row);
                                    }
                                }
                            }
                        }

                    }
                }
            }

        }

        //public IEnumerable<SyncRow> ReadRowsFromFile(string path, SyncTable shemaTable)
        //{
        //    if (!File.Exists(path))
        //        yield break;

        //    JsonSerializer serializer = new JsonSerializer();
        //    using var reader = new JsonTextReader(new StreamReader(path));
        //    while (reader.Read())
        //    {
        //        if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "t")
        //        {
        //            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        //            {
        //                if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "n")
        //                {
        //                    reader.Read();

        //                    while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        //                    {
        //                        if (reader.TokenType == JsonToken.PropertyName && reader.ValueType == typeof(string) && reader.Value != null && (string)reader.Value == "r")
        //                        {
        //                            // Go to children of the array
        //                            reader.Read();
        //                            // read all array
        //                            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        //                            {
        //                                if (reader.TokenType == JsonToken.StartArray)
        //                                {
        //                                    var array = serializer.Deserialize<object[]>(reader);
        //                                    yield return new SyncRow(shemaTable, array);
        //                                }
        //                            }
        //                        }

        //                    }
        //                }
        //            }
        //        }
        //    }

        //}

    }
}
