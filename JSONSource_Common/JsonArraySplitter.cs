﻿using com.webkingsoft.JSONSuite_Common.Model;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Microsoft.SqlServer.Dts.Runtime.Wrapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
#if LINQ_SUPPORTED
using System.Threading.Tasks;
using System.Windows.Forms;
#endif

namespace com.webkingsoft.JSONSuite_Common
{
    // TODO: add Custom UI, document the class
#if DTS130
    [DtsPipelineComponent(CurrentVersion = 1, DisplayName = "JSON Array Splitter", Description = "Splits raw json arrays into single raw json elements.", ComponentType = ComponentType.Transform, IconResource = "com.webkingsoft.JSONSuite_130.jsource.ico")] // UITypeName = "com.webkingsoft.JSONSource_Common.JSONSourceComponentUI,com.webkingsoft.JSONSource_130,Version=1.1.000.0,Culture=neutral"
#elif DTS120
    [DtsPipelineComponent(CurrentVersion = 1, DisplayName = "JSON Array Splitter", Description = "Splits raw json arrays into single raw json elements.", ComponentType = ComponentType.Transform, IconResource = "com.webkingsoft.JSONSuite_120.jsource.ico")] // UITypeName = "com.webkingsoft.JSONSource_Common.JSONSourceComponentUI,com.webkingsoft.JSONSource_120,Version=1.1.000.0,Culture=neutral"
#elif DTS110
    [DtsPipelineComponent(CurrentVersion = 1, DisplayName = "JSON Array Splitter", Description = "Splits raw json arrays into single raw json elements.", ComponentType = ComponentType.Transform, IconResource = "com.webkingsoft.JSONSuite_110.jsource.ico")] // UITypeName = "com.webkingsoft.JSONSource_Common.JSONSourceComponentUI,com.webkingsoft.JSONSource_110,Version=1.1.000.0,Culture=neutral"
#endif
    public class JsonArraySplitter : PipelineComponent
    {
        private static readonly string INPUT_LANE_NAME = "Raw json input";
        private static readonly string OUTPUT_LANE_NAME = "Array Elements";
        private static readonly string ERROR_LANE_NAME = "Error elements";

        /// Remember the lifecycle!
        /// AcquireConnections()
        /// Validate()
        /// ReleaseConnections()
        /// PrepareForExecute()
        /// AcquireConnections()
        /// PreExecute()
        /// PrimeOutput()
        /// ProcessInput()
        /// PostExecute()
        /// ReleaseConnections()
        /// Cleanup()

        // Public overrided methods
        public override void ProvideComponentProperties()
        {
            // This method is invoked by the design time when the component is added to the data flow for the very first time. 
            // In here, we need to configure the input and the output lanes and set the synchronization type.

            // Clear all inputs and custom props, plus setup outputs
            base.RemoveAllInputsOutputsAndCustomProperties();

            // Setup the input lane. 
            var input = ComponentMetaData.InputCollection.New();
            input.Name = INPUT_LANE_NAME;
            input.ErrorRowDisposition = DTSRowDisposition.RD_RedirectRow;

            // Setup the output lane. Every lane must be named, otherwise it won't work on the runtime.
            // We assume only one output will be available + 1 error output lane.
            var output = ComponentMetaData.OutputCollection.New();
            output.Name = OUTPUT_LANE_NAME;
            // output.ExclusionGroup = 1;

            // Configure Error output
            ComponentMetaData.UsesDispositions = true;
            var error = ComponentMetaData.OutputCollection.New();
            error.Name = ERROR_LANE_NAME;
            error.IsErrorOut = true;
            // error.ExclusionGroup = 1;

            // The ArraySplitter takes a line as input and produces 1+ output lines.
            // This means that the output is asynchronous to the input. By definition, this means that the associated input ID must be 0.
            output.SynchronousInputID = 0;
            error.SynchronousInputID = 0;

            // Custom property initialization
            var props = new ArraySplitterProperties();
            props.CopyToComponent(ComponentMetaData.CustomPropertyCollection);

            // The current implementation does not handle external metadata validation.
            // Therefore we must inform the design time about that.
            // TODO: Change this once we implement external metadata validation
            ComponentMetaData.ValidateExternalMetadata = false;
        }

        public override DTSValidationStatus Validate()
        {
            // ------ Input checks ------
            var inputValidation = ValidateInput();
            if (inputValidation != DTSValidationStatus.VS_ISVALID)
                return inputValidation;

            // ------ Output checks ------
            var outputValidation = ValidateOutput();
            if (outputValidation != DTSValidationStatus.VS_ISVALID)
                return outputValidation;

            // ------ Connection checks ------
            // No external connection is expected for now.

            // ------ Custom properties checks ------
            ArraySplitterProperties props;
            var customPropertiesValidation = ValidateCustomProperties(out props);
            if (customPropertiesValidation != DTSValidationStatus.VS_ISVALID)
                return customPropertiesValidation;

            // ------ Usage type -------
            var columnUsageTypeValidation = ValidateColumnsUsageType(props);
            if (columnUsageTypeValidation != DTSValidationStatus.VS_ISVALID)
                return columnUsageTypeValidation;

            // As very last step, invoke the base validation implementation which will check that every input 
            // column is associated to an output of the upstream component.
            return base.Validate();
        }

        public override void ReinitializeMetaData()
        {
            // Remove the previous output columns and the the relative metadata columns
            IDTSOutput100 output = ComponentMetaData.OutputCollection[OUTPUT_LANE_NAME];
            output.ExternalMetadataColumnCollection.RemoveAll();
            output.OutputColumnCollection.RemoveAll();

            // Make sure that every input column is treated as READONLY (for not interesting ones)
            // or READWRITE (for json array ones).
            AlignVirtualInputsToInputs();

            // Add them back from scratch
            CreateOutputAndMetaDataColumns(output);
        }
        
        public override void OnInputPathAttached(int inputID)
        {
            // We only have one input lane in this component, so we assume inputId that one.
            AlignVirtualInputsToInputs();
        }
        
        public override IDTSOutput100 InsertOutput(DTSInsertPlacement insertPlacement, int outputID)
        {
            throw new Exception("This component doesn't support any additional output");
        }

        public override IDTSInput100 InsertInput(DTSInsertPlacement insertPlacement, int inputID)
        {
            throw new Exception("This component doesn't support any additional input");
        }

        public override void DeleteInput(int inputID)
        {
            throw new Exception("You cannot delete the input lane");
        }

        public override void DeleteOutput(int outputID)
        {
            throw new Exception("You cannot delete the output lane");
        }

        // Private methods
        private void CreateOutputAndMetaDataColumns(IDTSOutput100 outlane)
        {
            // Since this component is asynchronous, we need to add output columns by ourself.
            // In fact, for asynchronous components, the row buffer is not shared.
            // We want to add an output column for every input column we have.
            IDTSInput100 input = ComponentMetaData.InputCollection[INPUT_LANE_NAME];
            IDTSOutput100 outLane = ComponentMetaData.OutputCollection[OUTPUT_LANE_NAME];
            var inputLane = ComponentMetaData.InputCollection[INPUT_LANE_NAME];
            foreach (IDTSInputColumn100 col in inputLane.InputColumnCollection)
            {
                var outCol = outLane.OutputColumnCollection.New();
                outCol.Name = col.Name;
                outCol.SetDataTypeProperties(col.DataType, col.Length, col.Precision, col.Scale, col.CodePage);
            }
        }

        private DTSValidationStatus ValidateColumnsUsageType(ArraySplitterProperties props)
        {
            bool pbCancel = false;
            // Make sure all the columns in the input component have been marked as READ-ONLY, except the one 
            // we are considering as JSON array. In case this is not the case, we might be able to recover by
            // using the ReinitializeMetadata.
            var inputLane = ComponentMetaData.InputCollection[INPUT_LANE_NAME];
            foreach (IDTSInputColumn100 col in inputLane.InputColumnCollection)
            {
                // If this is the selected json array column, it must be RW
                if (props.ArrayInputColumnName == col.Name && col.UsageType != DTSUsageType.UT_READWRITE)
                {
                    ComponentMetaData.FireError(0, ComponentMetaData.Name, "Column " + props.ArrayInputColumnName + " must be set as RW.", "", 0, out pbCancel);
                    return DTSValidationStatus.VS_NEEDSNEWMETADATA;
                }
                else if (props.ArrayInputColumnName != col.Name && col.UsageType != DTSUsageType.UT_READONLY)
                {
                    ComponentMetaData.FireError(0, ComponentMetaData.Name, "Column " + col.Name + " must be set as RO.", "", 0, out pbCancel);
                    return DTSValidationStatus.VS_NEEDSNEWMETADATA;
                }
            }
            return DTSValidationStatus.VS_ISVALID;
        }

        private DTSValidationStatus ValidateCustomProperties(out ArraySplitterProperties properties)
        {
            // TODO: handle validation for external metadata

            bool pbCancel = false;

            properties = new ArraySplitterProperties();
            properties.LoadFromComponent(ComponentMetaData.CustomPropertyCollection);

            // The user should have specified a valid input column where to fetch json from
            if (string.IsNullOrEmpty(properties.ArrayInputColumnName))
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Invalid input column specified.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISBROKEN;
            }

            // The selected input column name must reflect one of the available inputs
            // Moreover, the selected input column must be a valid STRING/TEXT type.
            bool found = false;
            foreach (IDTSInputColumn100 col in ComponentMetaData.InputCollection[INPUT_LANE_NAME].InputColumnCollection)
            {
                if (col.Name == properties.ArrayInputColumnName)
                {
                    found = true;

                    // Check the data type of the column
                    if (col.DataType != DataType.DT_NTEXT &&
                        col.DataType != DataType.DT_TEXT &&
                        col.DataType != DataType.DT_WSTR &&
                        col.DataType != DataType.DT_STR)
                    {

                        ComponentMetaData.FireError(0, ComponentMetaData.Name, "The selected input column must be in textual format (TEXT, NTEXT, STR, WSTR).", "", 0, out pbCancel);
                        return DTSValidationStatus.VS_ISBROKEN;
                    }

                    break;
                }
            }

            if (!found)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "The specified input column \"" + properties.ArrayInputColumnName +
                    "\" is not available among the columns of the input lane \"" + INPUT_LANE_NAME + "\"", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISBROKEN;
            }

            return DTSValidationStatus.VS_ISVALID;
        }

        private DTSValidationStatus ValidateInput()
        {
            // TODO: we might be able to return NEEDS_NEW_METADATA for some edge-cases. For now, just return CORRUPT.
            bool pbCancel = false;
            IDTSInput100 inputLane = null;

            // Only one input lane is expected
            if (ComponentMetaData.InputCollection.Count != 1)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Incorrect number of inputs.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            // Input lane must be named as INPUT_LANE_NAME
            try
            {
                inputLane = ComponentMetaData.InputCollection[INPUT_LANE_NAME];
                if (inputLane == null)
                    throw new Exception("Missing input lane");
            }
            catch (Exception e)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Incorrect number of inputs.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            // If a new column is added/removed from the upstream, we need to rebuild the metadata
            // accordingly. 
            if (!ComponentMetaData.AreInputColumnsValid)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Column metadata has changed.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_NEEDSNEWMETADATA;
            }

            return DTSValidationStatus.VS_ISVALID;
        }

        private DTSValidationStatus ValidateOutput()
        {
            // TODO: we might be able to return NEEDS_NEW_METADATA for some edge-cases. For now, just return CORRUPT.
            bool pbCancel = false;

            // We expect one output lane and one error lane
            if (ComponentMetaData.OutputCollection.Count != 2)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Incorrect number of outputs.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            // Output lane must be named as OUTPUT_LANE_NAME
            IDTSOutput100 outlane;
            try
            {
                outlane = ComponentMetaData.OutputCollection[OUTPUT_LANE_NAME];
                if (outlane == null)
                    throw new Exception("Missing output lane " + OUTPUT_LANE_NAME);
            }
            catch (Exception e)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Missing output lane " + OUTPUT_LANE_NAME, "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            if (outlane.IsErrorOut)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "The output lane " + OUTPUT_LANE_NAME + " should be used for output and not for error.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            // Error lane must be named as ERROR_LANE_NAME
            IDTSOutput100 errlane;
            try
            {
                errlane = ComponentMetaData.OutputCollection[ERROR_LANE_NAME];
                if (errlane == null)
                    throw new Exception("Missing output lane " + ERROR_LANE_NAME);
            }
            catch (Exception e)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "Missing output lane " + ERROR_LANE_NAME, "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            if (!errlane.IsErrorOut)
            {
                ComponentMetaData.FireError(0, ComponentMetaData.Name, "The output lane " + ERROR_LANE_NAME + " should be used for errors and not for result.", "", 0, out pbCancel);
                return DTSValidationStatus.VS_ISCORRUPT;
            }

            return DTSValidationStatus.VS_ISVALID;
        }
        
        private void AlignVirtualInputsToInputs()
        {
            ArraySplitterProperties model = new ArraySplitterProperties();
            model.LoadFromComponent(ComponentMetaData.CustomPropertyCollection);

            // TODO: Set the usage type of every column to READONLY, except for the one selected by the user as JSON raw input. 
            // That one should be used as READ-WRITE.
            IDTSInput100 input = ComponentMetaData.InputCollection[INPUT_LANE_NAME];
            var vInput = ComponentMetaData.InputCollection[INPUT_LANE_NAME].GetVirtualInput();
            foreach (IDTSVirtualInputColumn100 vCol in vInput.VirtualInputColumnCollection)
            {
                // Set every column to readonly except made for the one that we will interpret as JSON
                if (vCol.Name == model.ArrayInputColumnName)
                    SetUsageType(input.ID, vInput, vCol.LineageID, DTSUsageType.UT_READWRITE);
                else
                    SetUsageType(input.ID, vInput, vCol.LineageID, DTSUsageType.UT_READONLY);
            }
        }
        /*
        /// <summary>
        /// This function is used to attach a debugger. Just declare a boolean WK_DEBUG variable with TRUE value and invoke this function
        /// where you'd like to break.
        /// </summary>
        private void AttachDebugger()
        {
            DataType type;
            try
            {
                var value = Utils.GetVariable(VariableDispenser, "WK_DEBUG", out type);
                MessageBox.Show("Attach the debugger now! PID: " + System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            catch (Exception e)
            {
                // Do nothing
            }
        }*/

    }

}