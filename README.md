# Differentiable F#, executing using TensorFlow

The repo contains:

1.	TensorFlow API for the F# Programming Language

2.	A DSL implementation for `tf { ... }` that supports first-pass shape-checking/inference and other nice things

This is a POC that it is possible to build real, full-speed
TF graphs using a thin differentiable-DSL whose intended semantics are clear and relatively independent
of TF, while still achieving cohabitation and win-win with the TF ecosystem.


# The TensorFlow API for F# 

See `TensorFlow.FSharp`.  This API is designed in a similar way to `TensorFlowSharp`, but is implemented directly in F# and
contains some additional functionality.

# The Differentiable F# DSL

See `TensorFlow.FSharp.DSL`.  This DSL allows differentiation of F# code as follows:

```fsharp
    // Define a function which will be executed using TensorFlow
    let f x = x * x + v 4.0 * x 

    // Get the derivative of the function. This computes "x*2 + 4.0"
    let df x = DT.diff f x  

    // Run the derivative 
    df (v 3.0) |> DT.RunScalar // returns 6.0 + 4.0 = 10.0
```
For clarity code in the DSL which is executed via a TensorFlow graph is usually delineated with `tf { ... }`. This
is a convention, e.g.:
```fsharp
    // Define a function which will be executed using TensorFlow
    let f x = tf { return x * x + v 4.0 * x }
```
To differentiation a scalar function with multiple input variables:
```fsharp
    // Define a function which will be executed using TensorFlow
    // computes [ x1*x1*x3 + x2*x2*x2 + x3*x3*x1 + x1*x1 ]
    let f (xs: DT<'T>) = tf { return DT.Sum (xs * xs * DT.ReverseV2 xs) } 

    // Get the partial derivatives of the scalar function
    // computes [ 2*x1*x3 + x3*x3; 3*x2*x2; 2*x3*x1 + x1*x1 ]
    let df xs = DT.diff f xs   

    // Run the derivative 
    df (vec [ 3.0; 4.0; 5.0 ]) |> DT.RunArray // returns [ 55.0; 48.0; 39.0 ]
```
Below we show fitting a linear model to training data, by differentiating a loss function w.r.t. coefficients, and optimizing
using gradient descent (200 data points generated by linear  function, 10 parameters, linear model).
```fsharp
module ModelExample =
    let modelSize = 10
    let trainSize = 200
    let rnd = Random()

    /// The true function we use to generate the training data (also a linear model)
    let trueCoeffs = [| for i in 0 .. modelSize - 1 -> double i |]
    let trueFunction (xs: double[]) = Array.sum [| for i in 0 .. modelSize - 1 -> trueCoeffs.[i] * xs.[i] |]

    /// Make the training data
    let trainingInputs, trainingOutputs = 
        [| for i in 1 .. trainSize -> 
            let xs = [| for i in 0 .. modelSize - 1 -> rnd.NextDouble() |]
            xs, trueFunction xs |]
        |> Array.unzip

    /// Evaluate the model for input and coefficients
    let model (xs: DT<double>, coeffs: DT<double>) = 
        tf { return DT.Sum (xs * coeffs,axis= [| 1 |]) }

    /// Evaluate the loss function for the model w.r.t. a true output
    let loss (z: DT<double>) tgt = 
        tf { let dz = z - tgt in return DT.Sum (dz * dz) }

    // Gradient of the loss function w.r.t. the coefficients
    let dloss_dcoeffs (xs, y) coeffs = 
        let xnodes = batchOfVecs xs
        let ynode = batchOfScalars y
        let coeffnodes = vec coeffs
        let coffnodesBatch = batchExtend coeffnodes
        let z = loss (model (xnodes, coffnodesBatch)) ynode
        DT.gradient z coeffnodes 

    let rate = 0.001
    let step inputs (coeffs: double[]) = 
        let dz = dloss_dcoeffs inputs coeffs 
        let coeffs = (vec coeffs - v rate * dz) |> DT.RunArray
        printfn "coeffs = %A, dz = %A" coeffs dz
        coeffs

    let initialCoeffs = [| for i in 0 .. modelSize - 1 -> rnd.NextDouble()  * double modelSize|]

    // Train the inputs in one batch, up to 200 iterations
    let train inputs =
        initialCoeffs |> Seq.unfold (fun coeffs -> Some (coeffs, step inputs coeffs)) |> Seq.truncate 200 |> Seq.last

    train (trainingInputs, trainingOutputs)
    // Model parameters, close to the true parameters:
	//   [|0.007351991009; 1.004220712; 2.002591797; 3.018333918; 3.996983572; 4.981999364; 5.986054734; 
	//     7.005387338; 8.005461854; 8.991150034|]
```
More examples/tests are in [dsl-tests.fsx](https://github.com/fsprojects/TensorFlow.FSharp/blob/master/tests/dsl-tests.fsx).

* `tf { ... }` indicates a block of code expressed using the DSL and intended to be executed as a TensorFlow graph.  The
  use of `tf { ... }` is actually optional but recommended for clarity for all significant chunks of differentiable code.

* `DT` stands for `differentiable tensor` and the one type of `DT<_>` values are used to represent differentiable scalars, vectors, matrices and tensors.
  If you are familiar with the design of `DiffSharp` there are similarities here: DiffSharp defines `D` (differentiable scalar), `DV` (differentiable
  vector), `DM` (differentiable matrix).

* `DT.gradients` is used to get gradients of arbitrary outputs w.r.t. arbitrary inputs

* `DT.diff` is used to differentiate of `R^n -> R` scalar-valued functions (loss functions) w.r.t. multiple input variables. If 
  a scalar input is used, a single total deriative is returned. If a vector of inputs is used, a vector of
  partial derivatives are returned.

* `DT.jacobian` is used to differentiate `R^n -> R^2` vector-valued functions w.r.t. multiple input variables. A vector or
  matrix of partial derivatives is returned.

* Other gradient-based functions include `DT.grad`, `DT.curl`, `DT.hessian` and `DT.divergence`.

* In the prototype, all gradient-based functions are implemented using TensorFlow's `AddGradients`, i.e. the C++ implementation of
  gradients. Thus not all gradient-based functions are implemented efficiently for all inputs.

* `DT.*` is a DSL for expressing differentiable tensors using the TensorFlow fundamental building blocks.  The naming
  of operators in this DSL are currently TensorFLow specific and may change.

* A preliminary pass of shape inference is performed _before_ any TensorFlow operations are performed.  This
  allows you to check the shapes of your differentiable code indepentently of TensorFlow's shape computations.
  A shape inference system is used which allows for many shapes to be inferred and is akin to F# type inference.
  Not all TensorFlow automatic shape transformations are applied during shape inference.

  It is possible that at some future point this shape inference may be performed statically for F# code, or via a
  secondary tool.

While the above are toy examples, the approach scales (at least in principle) to the complete expression of deep neural networks
and full TensorFlow computation graphs. The links below show the implementation of a common DNN sample (the samples may not
yet run, this is wet paint):

* [NeuralStyleTransfer using F# TensorFlow API](https://github.com/fsprojects/TensorFlow.FSharp/blob/master/examples/NeuralStyleTransfer.fsx)

* [NeuralStyleTransfer in DSL form](https://github.com/fsprojects/TensorFlow.FSharp/blob/master/examples/NeuralStyleTransfer-dsl.fsx)

The design is intended to allow alternative execution with Torch or DiffSharp.
DiffSharp may be used once Tensors are available in that library.

# Building

First time only:

    fsiAnyCpu setup\setup.fsx

Then:

    dotnet restore
    dotnet build
    dotnet test
    dotnet pack

# Roadmap - Core API

* Port gradient and training/optimization code (from the Scala-Tensorflow)

* Port testing for core API

* GPU execution

* TPU execution

* Add docs

# Roadmap - DSL

* Switch to using ported gradient code when it is available in core API

* Hand-code or generate larger TF surface area in `tf { ... }` DSL

* Add proper testing for DSL 

* Consider control flow translation in DSL

* Add docs

* Add examples of how to do static graph building and analysis based on FCS, quotations and/or interpretation, e.g. for visualization

* Performance testing

# Roadmap - F# language

* Make use of `return` optional
