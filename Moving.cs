using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Threading.Tasks;
using System.Threading;

public class Movement : MonoBehaviour
{
    [SerializeField] Transform PlayerInputSpace = default;

    [HideInInspector]
    Rigidbody rb;

    [Header("Movement")]
    [SerializeField] private float Speed, Running, Peaking, Walking;
    [SerializeField] private float maxAcceleration;
    [SerializeField] private float maxAirAcceleration;
    Vector3 velocity, desiredVelocity;
    bool keepMomentum;
    [SerializeField] private AnimationCurve curve;
    [SerializeField] private float surfaceTime;
    [SerializeField] private float DesiredMoveSpeed;
    private float speedChangeFactor;
    bool isRunning, isPeaking;

    [Header("Jump")]
    [SerializeField] private float jumpForce;
    [SerializeField] private float jumpCooldown;
    [SerializeField] private float airMultiplier;
    [SerializeField, Range(0f, 5f)] private int maxAirJumps = 0;
    bool isJump;
    int jumpPhase;

    [Header("Ground Check")]
    [SerializeField] float playerHeight;
    [SerializeField] LayerMask Ground;
    int groundContactCount, steepContactCount;
    int stepsSinceLastGrounded, stepSinceLastJump;
    bool isGrounded => groundContactCount > 0;
    bool onSteep => steepContactCount > 0;

    [SerializeField, Range(0f, 90f)] private float maxGroundAngle = 25f, maxStairAngle = 50f;
    [SerializeField, Range(0f, 100f)] private float maxSnapSpeed = 100f;
    [SerializeField, Min(0f)] private float probeDistance = 1f;
    [SerializeField] private LayerMask probeMask = -1, stairsMask = -1;
    float minGroundDotProduct, minStairsDotProduct;
    Vector3 contactNormal, steepNormal;


    private void OnValidate()
    {
        minGroundDotProduct = Mathf.Cos(maxGroundAngle * Mathf.Deg2Rad);
        minStairsDotProduct = Mathf.Cos(maxStairAngle * Mathf.Deg2Rad);
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        OnValidate();
    }

    // Update is called once per frame
    void Update()
    {
        Vector2 playerInput;
        playerInput.x = Input.GetAxis("Horizontal");
        playerInput.y = Input.GetAxis("Vertical");
        playerInput = Vector2.ClampMagnitude(playerInput, 1f);

        if (PlayerInputSpace)
        {
            Vector3 forward = PlayerInputSpace.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = PlayerInputSpace.right;
            right.y = 0f;
            right.Normalize();
            desiredVelocity = (forward * playerInput.y + right * playerInput.x) * Speed;

        }
        else
        {
            desiredVelocity = new Vector3(playerInput.x, 0, playerInput.y) * Speed;
        }

        isJump |= Input.GetButtonDown("Jump");

        isPeaking = Input.GetKey(KeyCode.C);

        isRunning = Input.GetKey(KeyCode.E);
    }

    void FixedUpdate()
    {
        UpdateState();
        AdjustVelocity();
        SurfaceAlignment();

        if (isJump)
        {
            isJump = false;
            Jump();
        }

        if (isPeaking)
            GetState((int)MovementState.Peaking);
        else if (isRunning)
            GetState((int)MovementState.Running);
        else
            GetState((int)MovementState.Walking);

        rb.velocity = velocity;
        ClearState();
    }

    void AdjustVelocity()
    {
        Vector3 xAxis = ProjectOnContactPlane(Vector3.right).normalized;
        Vector3 zAxis = ProjectOnContactPlane(Vector3.forward).normalized;

        float currentX = Vector3.Dot(velocity, xAxis);
        float currentZ = Vector3.Dot(velocity, zAxis);

        float acceleration = isGrounded ? maxAcceleration : maxAirAcceleration;
        float maxSpeedChange = acceleration * Time.deltaTime;

        float newX = Mathf.MoveTowards(currentX, desiredVelocity.x, maxSpeedChange);
        float newZ = Mathf.MoveTowards(currentZ, desiredVelocity.z, maxSpeedChange);

        velocity += xAxis * (newX - currentX) + zAxis * (newZ - currentZ);
    }

    void Jump()
    {
        Vector3 jumpDirection;
        if (isGrounded)
        {
            jumpDirection = contactNormal;
        }
        else if (onSteep)
        {
            jumpDirection = steepNormal;
            jumpPhase = 0;
        }
        else if (maxAirJumps > 0 && jumpPhase <= maxAirJumps)
        {
            if (jumpPhase == 0)
            {
                jumpPhase = 1;
            }
            jumpDirection = contactNormal;
        }
        else
        {
            return;
        }
        stepSinceLastJump = 0;
        jumpPhase += 1;
        float jumpSpeed = Mathf.Sqrt(-2f * Physics.gravity.y * jumpForce);
        jumpDirection = (jumpDirection + Vector3.up).normalized;
        float alignSpeed = Vector3.Dot(velocity, contactNormal);
        if (alignSpeed > 0)
        {
            jumpSpeed = Mathf.Max(jumpSpeed - alignSpeed, 0f);
        }
        velocity.y += jumpSpeed;
    }

    void UpdateState()
    {
        stepsSinceLastGrounded += 1;
        stepSinceLastJump += 1;
        velocity = rb.velocity;
        if (isGrounded || SnapToGround() || CheckSteepContacts())
        {
            stepsSinceLastGrounded = 0;
            if (stepSinceLastJump > 1)
            {
                jumpPhase = 0;
            }
            if (groundContactCount > 1)
            {
                contactNormal.Normalize();
            }
        }
        else
        {
            contactNormal = Vector3.up;
        }
    }

    private void SurfaceAlignment()
    {
        Ray ray = new Ray(transform.position, -transform.up);
        RaycastHit info = new RaycastHit();
        Quaternion rotationRef = Quaternion.Euler(0f, 0f, 0f);
        if (Physics.Raycast(ray, out info, Ground))
        {
            rotationRef = Quaternion.Lerp(transform.rotation, Quaternion.FromToRotation(Vector3.up, info.normal), curve.Evaluate(surfaceTime));
            transform.rotation = Quaternion.Euler(rotationRef.eulerAngles.x, transform.eulerAngles.y, rotationRef.eulerAngles.z);
        }

    }

    void EvaluateCollision(Collision collision)
    {
        float minDot = GetMinDot(collision.gameObject.layer);
        int i = 0;
        Parallel.For(i, collision.contactCount, i =>
        {
            Vector3 normal = collision.GetContact(i).normal;
            if (normal.y >= minDot)
            {
                groundContactCount += 1;
                contactNormal += normal;
            }
            else if (normal.y > -0.01f)
            {
                steepContactCount += 1;
                steepNormal += normal;
            }
        });
    }

    private void OnCollisionEnter(Collision collision)
    {
        EvaluateCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        EvaluateCollision(collision);
    }

    private IEnumerator SmoothlyLerpMoveSpeed()
    {
        float time = 0f,
            difference = Mathf.Abs(DesiredMoveSpeed - Speed),
            startValue = Speed,
            boostFactor = speedChangeFactor;

        while (time < difference)
        {
            Speed = Mathf.Lerp(startValue, DesiredMoveSpeed, time / difference);
            time += Time.deltaTime * boostFactor;

            yield return null;
        }

        Speed = DesiredMoveSpeed;
        speedChangeFactor = 1f;
        keepMomentum = true;
    }

    Vector3 ProjectOnContactPlane(Vector3 vector)
    {
        return vector - contactNormal * Vector3.Dot(vector, contactNormal);
    }

    void ClearState()
    {
        groundContactCount = steepContactCount = 0;
        contactNormal = steepNormal = Vector3.zero;
    }

    float GetMinDot(int layer)
    {
        return (stairsMask & (1 << layer)) == 0 ?
            minGroundDotProduct : minStairsDotProduct;
    }

    bool SnapToGround()
    {
        if (stepsSinceLastGrounded > 1 || stepSinceLastJump <= 2)
        {
            return false;
        }
        float speed = velocity.magnitude;
        if (speed > maxSnapSpeed)
        {
            return false;
        }
        if (!Physics.Raycast(rb.position, Vector3.down,
            out RaycastHit hit, probeDistance, probeMask))
        {
            return false;
        }
        if (hit.normal.y < GetMinDot(hit.collider.gameObject.layer))
        {
            return false;
        }
        groundContactCount = 1;
        contactNormal = hit.normal;
        float dot = Vector3.Dot(velocity, hit.normal);
        if (dot > 0f)
        {
            velocity = (velocity - hit.normal * dot).normalized * speed;
        }
        return true;
    }

    bool CheckSteepContacts()
    {
        if (steepContactCount > 1)
        {
            steepNormal.Normalize();
            if (steepNormal.y >= minGroundDotProduct)
            {
                groundContactCount = 1;
                contactNormal = steepNormal;
                return true;
            }
        }
        return false;
    }

    public int GetState(int moveState) => moveState switch
    {
        (int)MovementState.Running => (int)(Speed = Running),
        (int)MovementState.Walking => (int)(Speed = Walking),
        (int)MovementState.Jumping => (int)(Speed = jumpForce),
        (int)MovementState.Peaking => (int)(Speed = Peaking),
        _ => (int)(Speed = 0f)
    };

    //delegate Vector3 playerVelocity(Vector3 velocity);
}

public enum MovementState
{
    Walking,
    Running,
    Jumping,
    Peaking
}
