// This example shows the NeuralStyles transfer model using the FM F#-for-AI-models DSL.
//
// See https://medium.com/tensorflow/neural-style-transfer-creating-art-with-deep-learning-using-tf-keras-and-eager-execution-7d541ac31398
//
// Build the Debug 'TensorFlow.FSharp.Tests' before using this

#I __SOURCE_DIRECTORY__
#r "netstandard"
#r "../tests/bin/Debug/net461/TensorFlow.FSharp.Proto.dll"
#r "../tests/bin/Debug/net461/TensorFlow.FSharp.dll"
#load "shared/NPYReaderWriter.fsx"


//------------------------------------------------------------------------------
// Preliminaries for F# scripting

open NPYReaderWriter
open System
open System.IO
open TensorFlow.FSharp
open TensorFlow.FSharp.DSL

// Check the process is 64-bit.  
if not System.Environment.Is64BitProcess then System.Environment.Exit(-1)

// Add the print helper for tensors in the REPL
fsi.AddPrintTransformer(DT.PrintTransform)

//------------------------------------------------------------------------------
// This is the NeuralStyles transfer model proper

[<TensorFlow>]
module NeuralStyles = 

    // Set up a convolution
    let conv_init_vars (out_channels:int, filter_size:int, is_transpose: bool, name) =
        let weights_shape = 
            if is_transpose then
                shape [ filter_size; filter_size; out_channels; -1 ]
            else
                shape [ filter_size; filter_size; -1; out_channels ]

        let truncatedNormal = DT.TruncatedNormal(weights_shape)
        variable (truncatedNormal * v 0.1) (name + "/weights") 

    let instance_norm (input: DT<double>, name) =
        use __ = DT.WithScope(name + "/instance_norm")
        let mu, sigma_sq = DT.Moments (input, axes=[1;2])
        let shift = variable (v 0.0) (name + "/shift")
        let scale = variable (v 1.0) (name + "/scale")
        let epsilon = v 0.001
        let normalized = (input - mu) / sqrt (sigma_sq + epsilon)
        scale * normalized + shift 

    // Set up a convolution layer in the DNN
    let conv_layer (input: DT<double>, out_channels, filter_size, stride, is_relu, name) = 
        let filters = conv_init_vars (out_channels, filter_size, false, name)
        let x = DT.Conv2D (input, filters, stride=stride)
        let x = instance_norm (x, name)
        if is_relu then 
            relu x 
        else 
            x 

    let residual_block (input, filter_size, name) = 
        let tmp = conv_layer(input, 128, filter_size, 1, true, name + "_c1")
        input + conv_layer(tmp, 128, filter_size, 1, false, name + "_c2") 

    let conv2D_transpose (input, filter, stride) = 
        DT.Conv2DBackpropInput(filter, input, stride, padding = "SAME") 
  
    let conv_transpose_layer (input: DT<double>, out_channels, filter_size, stride, name) =
        let filters = conv_init_vars (out_channels, filter_size, true, name)
        relu (instance_norm (conv2D_transpose (input, filters, stride), name))

    let to_pixel_value (input: DT<double>) = 
        tanh input * v 150.0 + (v 255.0 / v 2.0)

    // The style-transfer network
    let PretrainedFFStyleVGG images = 
        let x = conv_layer (images, 32, 9, 1, true, "conv1")
        let x = conv_layer (x, 64, 3, 2, true, "conv2")
        let x = conv_layer (x, 128, 3, 2, true, "conv3")
        let x = residual_block (x, 3, "resid1")
        let x = residual_block (x, 3, "resid2")
        let x = residual_block (x, 3, "resid3")
        let x = residual_block (x, 3, "resid4")
        let x = residual_block (x, 3, "resid5")
        let x = conv_transpose_layer (x, 64, 3, 2, "conv_t1")
        let x = conv_transpose_layer (x, 32, 3, 2, "conv_t2")
        let x = conv_layer (x, 3, 9, 1, false, "conv_t3")
        let x = to_pixel_value x
        let x = DT.ClipByValue (x, v 0.0, v 255.0)
        x

    // The average pixel in the decoding
    let mean_pixel  = pixel [| 123.68; 116.778; 103.939 |] 

    /// Read an image from the path as a tensor value
    let readImage imgPath = 
        let imgName = File.ReadAllBytes(imgPath) |> DT.CreateString
        // read the image
        let jpg = DT.DecodeJpeg(imgName) |> DT.Cast<double> |> DT.AssertShape(shape [474;  712; 3])
        // subtract the mean
        // TODO: these calls to DT.ExpandDims shouldn't be needed, the broadcast should happen implicitly
        jpg - (mean_pixel |> DT.ExpandDims|> DT.ExpandDims)

    /// Transform batches of images
    let transformImages weights images = 
        let result = PretrainedFFStyleVGG images |> DT.Cast<single> 
        DT.Eval(result, weights)

    /// transformImages is the complete model, taking a batch of images to a batch of images
    /// and then evaluating the model.
    ///
    /// As usual, the LiveCheck is placed at the point where the model is finished.
    /// 
    /// We use the empty weights for the LiveCheck
    [<LiveCheck>]
    let test() = 
        let dummyImages = DT.Stack [ for i in 1 .. 10 -> DT.Dummy [474;  712; 3] ]
        transformImages [| |] dummyImages 

    // Read the weights map
    let readWeights weightsFile = 
        let npz = readFromNPZ (File.ReadAllBytes weightsFile)
        [| for (k,(metadata, arr)) in npz do
              let shape = Shape.UserSpecified metadata.shape 
              let name = k.[0..k.Length-5]
              let value = DT.Reshape(DT.ConstArray arr, shape) |> DT.Cast<double> |> DT.Eval  :> DT
              yield (name, value) |]

    /// For convenience we add an entry point for one image
    let transformImage style imageName = 
        let image = readImage imageName
        let images = transformImages style (batch [ image ]) 
        images.[0,*,*,*] |> DT.toArray3D
 
// OK, now use the model on some sample data and pre-trained weights

// Compute the weights path
let imageFile imgName = Path.Combine(__SOURCE_DIRECTORY__,"../tests/examples", imgName)

let weightsFile style = Path.Combine(__SOURCE_DIRECTORY__,"../tests/pretrained", sprintf "fast_style_weights_%s.npz" style)

let processImage style imageName = 
    printfn "processing image %s in style %s" imageName style
    let weights = NeuralStyles.readWeights (weightsFile style)
    let image = NeuralStyles.transformImage weights (imageFile imageName) 
    let png = image  |> ImageWriter.arrayToPNG_HWC 
    let outfile = Path.Combine(__SOURCE_DIRECTORY__, sprintf "%s_in_%s_style2.png" imageName style)
    File.WriteAllBytes(outfile, png)

processImage "rain" "chicago.jpg" 
processImage "starry_night" "chicago.jpg" 
processImage "wave" "chicago.jpg" 

